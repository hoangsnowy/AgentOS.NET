// E1.8 — Jira Cloud over REST v3: a project's issues as a board's tickets. Auth is HTTP Basic with the account's e-mail
// and an API token (id.atlassian.com → Security → API tokens); the token is stored encrypted, never on the board row.
// Search uses the enhanced JQL endpoint (POST /rest/api/3/search/jql, token-paged — the old /search was retired),
// descriptions and comments are Atlassian Document Format, workflow moves go through the issue's transitions, and a
// pull request is linked as a remote link (globalId = its URL, so linking twice updates instead of duplicating).
//
// Maintainer rule: a comment's author is a maintainer when they hold the project's "Administrators" role
// (→ PROJECT_ADMIN) or "Developers" (team-managed "Member") role (→ PROJECT_MEMBER), directly or through a group in that
// role. Reading role actors needs the Administer Projects permission; without it every comment counts as discussion and
// the feed says so. Cached ten minutes per tenant + site + project.

using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Workspaces;

namespace AgentOs.Modules.Integration.Sources;

internal sealed class JiraRestClient
{
    internal const int MaxItems = 500;
    private const int PageSize = 100;
    private const int MaxGroupsExpanded = 5;
    private const int MaxGroupMembers = 200;

    internal static readonly FrozenSet<string> AdminRoles =
        new[] { "Administrators", "Administrator" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    internal static readonly FrozenSet<string> MemberRoles =
        new[] { "Developers", "Developer", "Member", "Members" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    internal const string RolesUnreadableNote =
        "The Jira project's roles couldn't be read (the account needs Administer Projects on it) — every comment counts as discussion this time.";

    internal static readonly string[] SearchFields = ["summary", "status", "labels", "assignee", "issuetype"];

    private static readonly TimeSpan RolesTtl = TimeSpan.FromMinutes(10);
    private static readonly ConcurrentDictionary<string, (DateTimeOffset At, FrozenDictionary<string, string> Roles)> RoleCache = new(StringComparer.Ordinal);

    private readonly HttpClient _http;
    private readonly TimeProvider _clock;

    public JiraRestClient(HttpClient http, TimeProvider? clock = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Jira Cloud API-token auth: HTTP Basic with the account e-mail as the user and the token as the password.</summary>
    internal static AuthenticationHeaderValue Basic(string email, string token) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(email.Trim() + ":" + token)));

    private static string Base(TrackerBinding b) =>
        b.JiraBaseUrl() ?? throw new InvalidOperationException("The Jira site must be an https URL, like https://contoso.atlassian.net.");

    private static string Key(TrackerBinding b, int number) => Uri.EscapeDataString(b.TicketKey(number));

    // ── Connect ──────────────────────────────────────────────────────────────────────────────────

    public async Task<BoardValidation> ValidateAsync(TrackerBinding binding, string token, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, $"{Base(binding)}/rest/api/3/project/{Uri.EscapeDataString(binding.Project.Trim())}", binding, token, null, ct)
            .ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return BoardValidation.Fail("Jira rejected the e-mail + API token. Create a token at id.atlassian.com → Security → API tokens, for the account whose e-mail you entered.");
        }
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return BoardValidation.Fail($"Jira project {binding.Project} was not found on {binding.Site} (or the account can't browse it).");
        }
        if (!response.IsSuccessStatusCode)
        {
            return BoardValidation.Fail($"Jira returned {(int)response.StatusCode} for project {binding.Project}.");
        }
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var name = Str(doc.RootElement, "name") ?? binding.Project;
        // The JQL must run too — a typo there is a 400 now, not at the first Autopilot pass.
        using var probe = await SendAsync(HttpMethod.Post, $"{Base(binding)}/rest/api/3/search/jql", binding, token,
            SearchBody(binding, null, 1), ct).ConfigureAwait(false);
        if (!probe.IsSuccessStatusCode)
        {
            return BoardValidation.Fail($"Jira refused the query ({(int)probe.StatusCode}): {ErrorMessage(await probe.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) ?? "check the JQL"}.");
        }
        return BoardValidation.Success(Str(doc.RootElement, "id") ?? binding.Project, name);
    }

    // ── Read ─────────────────────────────────────────────────────────────────────────────────────

    public async Task<BoardTickets> ReadTicketsAsync(TrackerBinding binding, string token, CancellationToken ct)
    {
        var tickets = new List<BoardTicket>();
        string? next = null;
        do
        {
            using var response = await SendAsync(HttpMethod.Post, $"{Base(binding)}/rest/api/3/search/jql", binding, token,
                SearchBody(binding, next, PageSize), ct).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Jira refused the query ({(int)response.StatusCode}): {ErrorMessage(json) ?? "check the JQL"}.");
            }
            var (page, token2) = ParseSearch(json, binding);
            tickets.AddRange(page);
            next = token2;
        }
        while (next is not null && tickets.Count < MaxItems);
        return new BoardTickets(tickets.Take(MaxItems).ToList());
    }

    /// <summary>The JQL a binding runs: its project, ANDed with the board's own JQL, newest first.</summary>
    internal static string BuildJql(TrackerBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var jql = "project = " + TrackerText.Quote(binding.Project.Trim(), '"', backslashEscape: true);
        var extra = binding.Filter?.Jql?.Trim();
        if (!string.IsNullOrEmpty(extra))
        {
            // A board's JQL may carry its own ORDER BY; ours wins (stable paging), theirs is dropped.
            var order = extra.LastIndexOf(" order by ", StringComparison.OrdinalIgnoreCase);
            if (order >= 0) { extra = extra[..order].Trim(); }
            if (extra.Length > 0) { jql += " AND (" + extra + ")"; }
        }
        return jql + " ORDER BY created DESC";
    }

    internal static string SearchBody(TrackerBinding binding, string? nextPageToken, int max)
    {
        var body = new JsonObject
        {
            ["jql"] = BuildJql(binding),
            ["maxResults"] = max,
            ["fields"] = new JsonArray([.. SearchFields.Select(f => (JsonNode)f)]),
        };
        if (nextPageToken is not null) { body["nextPageToken"] = nextPageToken; }
        return body.ToJsonString();
    }

    /// <summary>Pure parser for <c>POST /rest/api/3/search/jql</c>: the page's tickets (only the bound project's keys) and
    /// the next page's token (null on the last page).</summary>
    internal static (IReadOnlyList<BoardTicket> Tickets, string? Next) ParseSearch(string json, TrackerBinding binding)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var list = new List<BoardTicket>();
        if (root.TryGetProperty("issues", out var issues) && issues.ValueKind == JsonValueKind.Array)
        {
            foreach (var issue in issues.EnumerateArray())
            {
                var key = Str(issue, "key");
                if (binding.NumberOfKey(key) is not { } number) { continue; }   // moved to another project: not this board's
                var fields = issue.TryGetProperty("fields", out var f) && f.ValueKind == JsonValueKind.Object ? f : default;
                var status = fields.ValueKind == JsonValueKind.Object && fields.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.Object ? s : default;
                var statusName = status.ValueKind == JsonValueKind.Object ? Str(status, "name") : null;
                var category = status.ValueKind == JsonValueKind.Object && status.TryGetProperty("statusCategory", out var sc) ? Str(sc, "key") : null;
                var done = string.Equals(category, "done", StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrWhiteSpace(binding.DoneState) && string.Equals(statusName, binding.DoneState.Trim(), StringComparison.OrdinalIgnoreCase));
                var labels = fields.ValueKind == JsonValueKind.Object && fields.TryGetProperty("labels", out var l) && l.ValueKind == JsonValueKind.Array
                    ? l.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
                    : [];
                var assignee = fields.ValueKind == JsonValueKind.Object && fields.TryGetProperty("assignee", out var a) && a.ValueKind == JsonValueKind.Object
                    ? Str(a, "displayName") ?? Str(a, "accountId")
                    : null;
                var type = fields.ValueKind == JsonValueKind.Object && fields.TryGetProperty("issuetype", out var it) ? Str(it, "name") : null;
                // A Jira "Bug" is a fix: the type: label the PR title and ticket flow read.
                if (type is not null && !labels.Any(x => x.StartsWith("type:", StringComparison.OrdinalIgnoreCase)))
                {
                    labels.Add("type:" + type.Trim().ToLowerInvariant().Replace(' ', '-'));
                }
                list.Add(new BoardTicket(
                    ItemNodeId: Str(issue, "id") ?? key!,
                    Number: number,
                    Title: (fields.ValueKind == JsonValueKind.Object ? Str(fields, "summary") : null) ?? key!,
                    State: done ? "CLOSED" : "OPEN",
                    Kind: BoardTicketKind.Issue,
                    RepoOwner: null,
                    RepoName: null,
                    HtmlUrl: binding.TicketUrl(number),
                    Labels: labels,
                    Status: statusName,
                    AssigneeLogin: assignee,
                    Key: binding.TicketKey(number)));
            }
        }
        var last = root.TryGetProperty("isLast", out var il) && il.ValueKind == JsonValueKind.True;
        var next = last ? null : Str(root, "nextPageToken");
        return (list, next);
    }

    /// <summary>The issue's description and newest comments, authors mapped to roles.</summary>
    public async Task<TicketContent> ReadTicketAsync(TrackerBinding binding, string tenantId, string token, int number, int maxComments, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, $"{Base(binding)}/rest/api/3/issue/{Key(binding, number)}?fields=description", binding, token, null, ct)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) { return TicketContent.BodyOnly(null); }
        response.EnsureSuccessStatusCode();
        string? body;
        using (var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)))
        {
            body = doc.RootElement.TryGetProperty("fields", out var f) && f.TryGetProperty("description", out var d)
                ? TrackerText.AdfToText(d)
                : null;
        }
        if (string.IsNullOrWhiteSpace(body)) { body = null; }
        if (maxComments <= 0) { return TicketContent.BodyOnly(body); }

        using var comments = await SendAsync(HttpMethod.Get,
            $"{Base(binding)}/rest/api/3/issue/{Key(binding, number)}/comment?orderBy=-created&maxResults={Math.Min(maxComments * 3, 100).ToString(CultureInfo.InvariantCulture)}",
            binding, token, null, ct).ConfigureAwait(false);
        if (!comments.IsSuccessStatusCode)
        {
            return TicketContent.BodyOnly(body, "The issue's comments couldn't be read from Jira — the agents work from its description only this time.");
        }
        var raw = ParseComments(await comments.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var (roles, note) = raw.Count == 0 ? (FrozenDictionary<string, string>.Empty, null) : await RolesAsync(binding, tenantId, token, ct).ConfigureAwait(false);
        var list = raw.Select(c => c.Comment with { AuthorAssociation = roles.GetValueOrDefault(c.AccountId, "NONE") })
            .OrderBy(c => c.CreatedAt).ToList();
        return new TicketContent(body, list) { CommentsNote = note };
    }

    /// <summary>Pure parser for <c>GET /rest/api/3/issue/{key}/comment</c>: each comment with its author's account id.</summary>
    internal static IReadOnlyList<(TicketDiscussionComment Comment, string AccountId)> ParseComments(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("comments", out var comments) || comments.ValueKind != JsonValueKind.Array) { return []; }
        var list = new List<(TicketDiscussionComment, string)>(comments.GetArrayLength());
        foreach (var c in comments.EnumerateArray())
        {
            if (c.ValueKind != JsonValueKind.Object) { continue; }
            var author = c.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;
            var accountId = author.ValueKind == JsonValueKind.Object ? Str(author, "accountId") ?? string.Empty : string.Empty;
            var name = author.ValueKind == JsonValueKind.Object ? Str(author, "displayName") ?? string.Empty : string.Empty;
            var bot = author.ValueKind == JsonValueKind.Object && string.Equals(Str(author, "accountType"), "app", StringComparison.OrdinalIgnoreCase);
            var text = c.TryGetProperty("body", out var b) ? TrackerText.AdfToText(b) : string.Empty;
            var created = DateTimeOffset.TryParse(Str(c, "created"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : DateTimeOffset.MinValue;
            list.Add((new TicketDiscussionComment(name.Replace(' ', '-'), "NONE", created, text, bot), accountId));
        }
        return list;
    }

    // ── Roles (maintainer rule) ──────────────────────────────────────────────────────────────────

    private async Task<(FrozenDictionary<string, string> Roles, string? Note)> RolesAsync(TrackerBinding binding, string tenantId, string token, CancellationToken ct)
    {
        var key = $"{tenantId}\n{binding.Site}\n{binding.Project}".ToUpperInvariant();
        if (RoleCache.TryGetValue(key, out var hit) && _clock.GetUtcNow() - hit.At < RolesTtl) { return (hit.Roles, null); }
        try
        {
            var project = Uri.EscapeDataString(binding.Project.Trim());
            using var rolesResponse = await SendAsync(HttpMethod.Get, $"{Base(binding)}/rest/api/3/project/{project}/role", binding, token, null, ct).ConfigureAwait(false);
            if (!rolesResponse.IsSuccessStatusCode) { return (FrozenDictionary<string, string>.Empty, RolesUnreadableNote); }
            var roleIds = ParseRoleIds(await rolesResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            var groups = 0;
            // Admins first so a person in both roles is an admin.
            foreach (var (name, id) in roleIds.OrderBy(r => AdminRoles.Contains(r.Name) ? 0 : 1))
            {
                var association = AdminRoles.Contains(name) ? TicketDiscussion.ProjectAdminAssociation
                    : MemberRoles.Contains(name) ? TicketDiscussion.ProjectMemberAssociation : null;
                if (association is null) { continue; }
                using var actorsResponse = await SendAsync(HttpMethod.Get, $"{Base(binding)}/rest/api/3/project/{project}/role/{id.ToString(CultureInfo.InvariantCulture)}",
                    binding, token, null, ct).ConfigureAwait(false);
                if (!actorsResponse.IsSuccessStatusCode) { return (FrozenDictionary<string, string>.Empty, RolesUnreadableNote); }
                var (users, groupIds) = ParseRoleActors(await actorsResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                foreach (var user in users) { map.TryAdd(user, association); }
                foreach (var groupId in groupIds)
                {
                    if (++groups > MaxGroupsExpanded) { break; }
                    foreach (var member in await GroupMembersAsync(binding, token, groupId, ct).ConfigureAwait(false)) { map.TryAdd(member, association); }
                }
            }
            var frozen = map.ToFrozenDictionary(StringComparer.Ordinal);
            RoleCache[key] = (_clock.GetUtcNow(), frozen);
            return (frozen, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return (FrozenDictionary<string, string>.Empty, RolesUnreadableNote);
        }
    }

    private async Task<IReadOnlyList<string>> GroupMembersAsync(TrackerBinding binding, string token, string groupId, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get,
            $"{Base(binding)}/rest/api/3/group/member?groupId={Uri.EscapeDataString(groupId)}&maxResults={MaxGroupMembers.ToString(CultureInfo.InvariantCulture)}",
            binding, token, null, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) { return []; }
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return doc.RootElement.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Select(v => Str(v, "accountId")).OfType<string>().ToList()
            : [];
    }

    /// <summary>Pure parser for <c>GET /project/{key}/role</c> (role name → self URL): each role's name and id. The id is
    /// read from the URL's last segment and the actors are fetched from OUR base URL — a URL from a response is never followed.</summary>
    internal static IReadOnlyList<(string Name, int Id)> ParseRoleIds(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) { return []; }
        var list = new List<(string, int)>();
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            var url = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null;
            var slash = url?.LastIndexOf('/') ?? -1;
            if (slash >= 0 && int.TryParse(url.AsSpan(slash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var id)) { list.Add((p.Name, id)); }
        }
        return list;
    }

    /// <summary>Pure parser for <c>GET /project/{key}/role/{id}</c>: the user account ids and the group ids in the role.</summary>
    internal static (IReadOnlyList<string> Users, IReadOnlyList<string> Groups) ParseRoleActors(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var users = new List<string>();
        var groups = new List<string>();
        if (doc.RootElement.TryGetProperty("actors", out var actors) && actors.ValueKind == JsonValueKind.Array)
        {
            foreach (var actor in actors.EnumerateArray())
            {
                if (actor.TryGetProperty("actorUser", out var u) && Str(u, "accountId") is { Length: > 0 } accountId) { users.Add(accountId); }
                else if (actor.TryGetProperty("actorGroup", out var g) && Str(g, "groupId") is { Length: > 0 } groupId) { groups.Add(groupId); }
            }
        }
        return (users, groups);
    }

    // ── Write ────────────────────────────────────────────────────────────────────────────────────

    public async Task<bool> CommentAsync(TrackerBinding binding, string token, int number, string markdown, CancellationToken ct)
    {
        var body = new JsonObject { ["body"] = TrackerText.TextToAdf(markdown) }.ToJsonString();
        using var response = await SendAsync(HttpMethod.Post, $"{Base(binding)}/rest/api/3/issue/{Key(binding, number)}/comment", binding, token, body, ct)
            .ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    /// <summary>Move the issue through the transition named <paramref name="target"/> (or the one leading to a status of
    /// that name). False when the workflow offers none from the issue's current status.</summary>
    public async Task<bool> TransitionAsync(TrackerBinding binding, string token, int number, string target, CancellationToken ct)
    {
        var url = $"{Base(binding)}/rest/api/3/issue/{Key(binding, number)}/transitions";
        string? id;
        using (var list = await SendAsync(HttpMethod.Get, url, binding, token, null, ct).ConfigureAwait(false))
        {
            if (!list.IsSuccessStatusCode) { return false; }
            id = PickTransition(await list.Content.ReadAsStringAsync(ct).ConfigureAwait(false), target);
        }
        if (id is null) { return false; }
        var body = new JsonObject { ["transition"] = new JsonObject { ["id"] = id } }.ToJsonString();
        using var response = await SendAsync(HttpMethod.Post, url, binding, token, body, ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    /// <summary>Pure pick from <c>GET …/transitions</c>: the transition named <paramref name="target"/>, else the one to a
    /// status of that name (case-insensitive).</summary>
    internal static string? PickTransition(string json, string target)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("transitions", out var transitions) || transitions.ValueKind != JsonValueKind.Array) { return null; }
        var t = target.Trim();
        string? byStatus = null;
        foreach (var tr in transitions.EnumerateArray())
        {
            if (string.Equals(Str(tr, "name"), t, StringComparison.OrdinalIgnoreCase)) { return Str(tr, "id"); }
            if (byStatus is null && tr.TryGetProperty("to", out var to) && string.Equals(Str(to, "name"), t, StringComparison.OrdinalIgnoreCase)) { byStatus = Str(tr, "id"); }
        }
        return byStatus;
    }

    public async Task<bool> LinkAsync(TrackerBinding binding, string token, int number, string url, string title, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["globalId"] = url,
            ["object"] = new JsonObject { ["url"] = url, ["title"] = title, ["summary"] = "Pull request opened by an AgentOS.NET agent" },
        }.ToJsonString();
        using var response = await SendAsync(HttpMethod.Post, $"{Base(binding)}/rest/api/3/issue/{Key(binding, number)}/remotelink", binding, token, body, ct)
            .ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    public async Task<CreatedTicket> CreateAsync(TrackerBinding binding, string token, TicketDraft draft, CancellationToken ct)
    {
        // Jira labels can't hold spaces.
        var labels = draft.Labels.Concat(draft.AiReady ? [StandardLabels.AiReady] : [])
            .Select(l => l.Trim().Replace(' ', '-')).Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);
        var body = new JsonObject
        {
            ["fields"] = new JsonObject
            {
                ["project"] = new JsonObject { ["key"] = binding.Project.Trim() },
                ["summary"] = draft.Title.Length > 255 ? draft.Title[..255] : draft.Title,
                ["description"] = TrackerText.TextToAdf(draft.Body),
                ["issuetype"] = new JsonObject { ["name"] = "Task" },
                ["labels"] = new JsonArray([.. labels.Select(l => (JsonNode)l)]),
            },
        }.ToJsonString();
        using var response = await SendAsync(HttpMethod.Post, $"{Base(binding)}/rest/api/3/issue", binding, token, body, ct).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Jira couldn't create the issue ({(int)response.StatusCode}): {ErrorMessage(json) ?? "check the project has a Task issue type"}.");
        }
        using var doc = JsonDocument.Parse(json);
        var key = Str(doc.RootElement, "key");
        var number = binding.NumberOfKey(key) ?? throw new InvalidOperationException($"Jira created {key}, which isn't in project {binding.Project}.");
        var id = Str(doc.RootElement, "id") ?? key!;
        return new CreatedTicket(number, id, binding.TicketUrl(number), id, draft.Title);
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────────────────────────

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, TrackerBinding binding, string token, string? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = Basic(binding.Account ?? string.Empty, token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }
        return await _http.SendAsync(request, ct).ConfigureAwait(false);
    }

    /// <summary>Jira's error text: <c>errorMessages[]</c> and <c>errors{}</c> values, joined.</summary>
    internal static string? ErrorMessage(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) { return null; }
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) { return null; }
            var parts = new List<string>();
            if (doc.RootElement.TryGetProperty("errorMessages", out var m) && m.ValueKind == JsonValueKind.Array)
            {
                parts.AddRange(m.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!));
            }
            if (doc.RootElement.TryGetProperty("errors", out var e) && e.ValueKind == JsonValueKind.Object)
            {
                parts.AddRange(e.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).Select(p => $"{p.Name}: {p.Value.GetString()}"));
            }
            return parts.Count == 0 ? null : string.Join("; ", parts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
