// E1.8 — Azure DevOps Boards over REST (api-version 7.1): work items as a board's tickets. WIQL picks the ids (project +
// area / iteration / state / tags), a batch read fetches their fields, comments come from the work item comments API,
// state moves and pull request links are JSON-Patch updates. Raw HTTP like AzureDevOpsRestClient (the official SDK
// doesn't resolve under net10); every response parser is a pure static method, unit-tested on API-shaped fixtures.
//
// Maintainer rule: a comment's author is a maintainer when the identity is a member (direct or nested) of the project's
// "Project Administrators" (→ PROJECT_ADMIN) or "Contributors" (→ PROJECT_MEMBER) group. Both group descriptors and the
// authors' memberships are read from the Identities API and cached for ten minutes per tenant + project. When they can't
// be read (a PAT without Identity (Read)), every comment counts as discussion and the feed says so — never the reverse.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Workspaces;

namespace AgentOs.Modules.Integration.Sources;

internal sealed partial class AzureDevOpsBoardsClient
{
    internal const string ApiVersion = "7.1";
    internal const string CommentsApiVersion = "7.1-preview.4";
    internal const string AdminsGroup = "Project Administrators";
    internal const string ContributorsGroup = "Contributors";

    // WIQL returns at most this many ids; the batch read takes 200 a call.
    internal const int MaxItems = 500;
    private const int BatchSize = 200;

    internal const string RolesUnreadableNote =
        "The Azure DevOps project's groups couldn't be read (the token needs Identity (Read)) — every comment counts as discussion this time.";

    private static readonly TimeSpan RolesTtl = TimeSpan.FromMinutes(10);
    private static readonly ConcurrentDictionary<string, (DateTimeOffset At, string? Admins, string? Contributors)> GroupCache = new(StringComparer.Ordinal);

    private readonly HttpClient _http;
    private readonly TimeProvider _clock;

    public AzureDevOpsBoardsClient(HttpClient http, TimeProvider? clock = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>PAT auth is HTTP Basic with an empty user name and the PAT as the password.</summary>
    internal static AuthenticationHeaderValue Basic(string pat) => new("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + pat)));

    internal static string OrgBase(TrackerBinding b) =>
        $"https://{b.Host ?? "dev.azure.com"}/{Uri.EscapeDataString(b.Site.Trim())}";

    internal static string ProjectBase(TrackerBinding b) => $"{OrgBase(b)}/{Uri.EscapeDataString(b.Project.Trim())}";

    // Identities live on vssps.dev.azure.com in the cloud; Azure DevOps Server serves them on its own host.
    internal static string IdentityBase(TrackerBinding b) =>
        b.Host is null ? $"https://vssps.dev.azure.com/{Uri.EscapeDataString(b.Site.Trim())}" : OrgBase(b);

    // ── Connect ──────────────────────────────────────────────────────────────────────────────────

    public async Task<BoardValidation> ValidateAsync(TrackerBinding binding, string pat, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, $"{OrgBase(binding)}/_apis/projects/{Uri.EscapeDataString(binding.Project.Trim())}?api-version={ApiVersion}", pat, null, null, ct)
            .ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NonAuthoritativeInformation)
        {
            return BoardValidation.Fail("Azure DevOps rejected the token. It needs Work Items (Read & write) on the organization.");
        }
        if (!response.IsSuccessStatusCode)
        {
            return BoardValidation.Fail($"Azure DevOps returned {(int)response.StatusCode} for project {binding.Site}/{binding.Project}. Check the organization and project names.");
        }
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var id = Str(doc.RootElement, "id") ?? binding.Project;
        var name = Str(doc.RootElement, "name") ?? binding.Project;
        // The query itself must also run (a bad area path is a 400 here, not at the first Autopilot pass).
        var probe = await QueryIdsAsync(binding, pat, ct).ConfigureAwait(false);
        return probe.Error is { } error ? BoardValidation.Fail(error) : BoardValidation.Success(id, name);
    }

    // ── Read ─────────────────────────────────────────────────────────────────────────────────────

    public async Task<BoardTickets> ReadTicketsAsync(TrackerBinding binding, string pat, CancellationToken ct)
    {
        var (ids, error) = await QueryIdsAsync(binding, pat, ct).ConfigureAwait(false);
        if (error is not null) { throw new InvalidOperationException(error); }
        var items = new List<BoardTicket>(ids.Count);
        foreach (var chunk in ids.Chunk(BatchSize))
        {
            var url = $"{ProjectBase(binding)}/_apis/wit/workitems?ids={string.Join(',', chunk.Select(i => i.ToString(CultureInfo.InvariantCulture)))}"
                + $"&fields=System.Id,System.Title,System.State,System.Tags,System.AssignedTo,System.WorkItemType&errorPolicy=Omit&api-version={ApiVersion}";
            using var response = await SendAsync(HttpMethod.Get, url, pat, null, null, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            items.AddRange(ParseWorkItems(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false), binding));
        }
        // WIQL's order (newest change first) survives the batch read's own ordering.
        var rank = ids.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        return new BoardTickets([.. items.OrderBy(t => rank.GetValueOrDefault(t.Number ?? 0, int.MaxValue))]);
    }

    private async Task<(IReadOnlyList<int> Ids, string? Error)> QueryIdsAsync(TrackerBinding binding, string pat, CancellationToken ct)
    {
        var body = new JsonObject { ["query"] = BuildWiql(binding) }.ToJsonString();
        using var response = await SendAsync(HttpMethod.Post, $"{ProjectBase(binding)}/_apis/wit/wiql?$top={MaxItems}&api-version={ApiVersion}", pat,
            body, "application/json", ct).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return ([], $"Azure DevOps refused the work item query ({(int)response.StatusCode}): {ErrorMessage(json) ?? "check the area / iteration paths and states"}.");
        }
        return (ParseWiqlIds(json), null);
    }

    /// <summary>The WIQL for a binding: the project's work items, minus removed ones, narrowed by the filter, newest change first.</summary>
    internal static string BuildWiql(TrackerBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var f = binding.Filter ?? TrackerFilter.None;
        var sb = new StringBuilder("SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project AND [System.State] <> 'Removed'");
        if (!string.IsNullOrWhiteSpace(f.AreaPath))
        {
            sb.Append(" AND [System.AreaPath] UNDER ").Append(TrackerText.Quote(f.AreaPath.Trim(), '\'', backslashEscape: false));
        }
        if (!string.IsNullOrWhiteSpace(f.IterationPath))
        {
            var iteration = f.IterationPath.Trim();
            sb.Append(" AND [System.IterationPath] ").Append(CurrentIteration().IsMatch(iteration)
                ? "= " + iteration
                : "UNDER " + TrackerText.Quote(iteration, '\'', backslashEscape: false));
        }
        var states = (f.States ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => TrackerText.Quote(s.Trim(), '\'', false)).ToList();
        if (states.Count > 0) { sb.Append(" AND [System.State] IN (").Append(string.Join(", ", states)).Append(')'); }
        foreach (var tag in (f.Tags ?? []).Where(t => !string.IsNullOrWhiteSpace(t)))
        {
            sb.Append(" AND [System.Tags] CONTAINS ").Append(TrackerText.Quote(tag.Trim(), '\'', false));
        }
        return sb.Append(" ORDER BY [System.ChangedDate] DESC").ToString();
    }

    // @CurrentIteration, optionally with its team: @CurrentIteration('[Shop]\Shop Team'). Anything else is a path.
    [GeneratedRegex(@"^@CurrentIteration(?:\('\[[^\]'\r\n]{1,256}\]\\[^'\r\n]{1,256}'\))?(?:\s*[+-]\s*\d{1,2})?$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex CurrentIteration();

    /// <summary>Pure parser for the WIQL response: the work item ids, in query order.</summary>
    internal static IReadOnlyList<int> ParseWiqlIds(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("workItems", out var items) || items.ValueKind != JsonValueKind.Array) { return []; }
        return items.EnumerateArray()
            .Select(item => item.TryGetProperty("id", out var id) && id.TryGetInt32(out var n) ? n : 0)
            .Where(n => n > 0)
            .Take(MaxItems)
            .ToList();
    }

    /// <summary>Pure parser for the batch work item read (<c>GET …/wit/workitems?ids=…</c>).</summary>
    internal static IReadOnlyList<BoardTicket> ParseWorkItems(string json, TrackerBinding binding)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array) { return []; }
        var done = DoneStates(binding);
        var tickets = new List<BoardTicket>(value.GetArrayLength());
        foreach (var wi in value.EnumerateArray())
        {
            if (wi.ValueKind != JsonValueKind.Object || !wi.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out var id)) { continue; }
            if (!wi.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Object) { continue; }
            var state = Str(fields, "System.State") ?? string.Empty;
            var assignee = fields.TryGetProperty("System.AssignedTo", out var a) && a.ValueKind == JsonValueKind.Object
                ? Str(a, "uniqueName") ?? Str(a, "displayName")
                : null;
            tickets.Add(new BoardTicket(
                ItemNodeId: id.ToString(CultureInfo.InvariantCulture),
                Number: id,
                Title: Str(fields, "System.Title") ?? $"Work item {id}",
                State: done.Contains(state) ? "CLOSED" : "OPEN",
                Kind: BoardTicketKind.Issue,
                RepoOwner: null,
                RepoName: null,
                HtmlUrl: binding.TicketUrl(id),
                Labels: TrackerText.SplitTags(Str(fields, "System.Tags")),
                Status: state.Length == 0 ? null : state,
                AssigneeLogin: assignee,
                Key: binding.TicketKey(id)));
        }
        return tickets;
    }

    // Done for Autopilot: the process templates' closed states, plus the binding's own done state.
    private static HashSet<string> DoneStates(TrackerBinding binding)
    {
        var done = new HashSet<string>(["Closed", "Done", "Removed", "Completed"], StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(binding.DoneState)) { done.Add(binding.DoneState.Trim()); }
        return done;
    }

    /// <summary>The work item's description (+ acceptance criteria) and its newest comments, authors mapped to roles.</summary>
    public async Task<TicketContent> ReadTicketAsync(TrackerBinding binding, string tenantId, string pat, int id, int maxComments, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get,
            $"{ProjectBase(binding)}/_apis/wit/workitems/{id.ToString(CultureInfo.InvariantCulture)}?fields=System.Description,Microsoft.VSTS.Common.AcceptanceCriteria,Microsoft.VSTS.TCM.ReproSteps&api-version={ApiVersion}",
            pat, null, null, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) { return TicketContent.BodyOnly(null); }
        response.EnsureSuccessStatusCode();
        var body = ParseDescription(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if (maxComments <= 0) { return TicketContent.BodyOnly(body); }

        using var comments = await SendAsync(HttpMethod.Get,
            $"{ProjectBase(binding)}/_apis/wit/workItems/{id.ToString(CultureInfo.InvariantCulture)}/comments?$top={Math.Min(maxComments * 3, 200)}&order=desc&api-version={CommentsApiVersion}",
            pat, null, null, ct).ConfigureAwait(false);
        if (!comments.IsSuccessStatusCode)
        {
            return TicketContent.BodyOnly(body, "The work item's comments couldn't be read from Azure DevOps — the agents work from its description only this time.");
        }
        var raw = ParseComments(await comments.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var (roles, rolesNote) = await RolesAsync(binding, tenantId, pat, raw.Select(c => c.AuthorId).Where(i => i.Length > 0).Distinct().ToList(), ct)
            .ConfigureAwait(false);
        var list = raw.Select(c => c.Comment with { AuthorAssociation = roles.GetValueOrDefault(c.AuthorId, "NONE") })
            .OrderBy(c => c.CreatedAt).ToList();
        return new TicketContent(body, list) { CommentsNote = rolesNote };
    }

    /// <summary>Pure parser for the work item read: the description, then the acceptance criteria / repro steps, as text.</summary>
    internal static string? ParseDescription(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Object) { return null; }
        var parts = new List<string>(3);
        var description = TrackerText.HtmlToText(Str(fields, "System.Description"));
        if (description.Length > 0) { parts.Add(description); }
        var repro = TrackerText.HtmlToText(Str(fields, "Microsoft.VSTS.TCM.ReproSteps"));
        if (repro.Length > 0) { parts.Add("Repro steps:\n" + repro); }
        var criteria = TrackerText.HtmlToText(Str(fields, "Microsoft.VSTS.Common.AcceptanceCriteria"));
        if (criteria.Length > 0) { parts.Add("Acceptance criteria:\n" + criteria); }
        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }

    /// <summary>Pure parser for the comments API: each comment with its author's identity id (the role is mapped later).</summary>
    internal static IReadOnlyList<(TicketDiscussionComment Comment, string AuthorId)> ParseComments(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("comments", out var comments) || comments.ValueKind != JsonValueKind.Array) { return []; }
        var list = new List<(TicketDiscussionComment, string)>(comments.GetArrayLength());
        foreach (var c in comments.EnumerateArray())
        {
            if (c.ValueKind != JsonValueKind.Object) { continue; }
            if (c.TryGetProperty("isDeleted", out var del) && del.ValueKind == JsonValueKind.True) { continue; }
            var raw = Str(c, "text") ?? string.Empty;
            // Markdown comments come back as written; HTML ones are flattened — but AgentOS.NET's own (signed) text is kept
            // as it is, so its signature still marks it as ours.
            var markdown = string.Equals(Str(c, "format"), "markdown", StringComparison.OrdinalIgnoreCase);
            var text = markdown || TicketDiscussion.IsAgentOsComment(raw) ? raw : TrackerText.HtmlToText(raw);
            var by = c.TryGetProperty("createdBy", out var cb) && cb.ValueKind == JsonValueKind.Object ? cb : default;
            var login = by.ValueKind == JsonValueKind.Object ? Str(by, "uniqueName") ?? Str(by, "displayName") ?? string.Empty : string.Empty;
            var display = by.ValueKind == JsonValueKind.Object ? Str(by, "displayName") ?? string.Empty : string.Empty;
            var authorId = by.ValueKind == JsonValueKind.Object ? Str(by, "id") ?? string.Empty : string.Empty;
            var created = DateTimeOffset.TryParse(Str(c, "createdDate"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : DateTimeOffset.MinValue;
            // Service identities ("Project Collection Build Service (contoso)", an app's identity) are bots.
            var bot = display.Contains("Build Service", StringComparison.OrdinalIgnoreCase) || Guid.TryParse(login, out _);
            list.Add((new TicketDiscussionComment(LoginOf(login), "NONE", created, text, bot), authorId));
        }
        return list;
    }

    // The part of an e-mail / DOMAIN\user before the domain — what a login looks like in the prompt (it allows [a-z0-9-_]).
    private static string LoginOf(string uniqueName)
    {
        var at = uniqueName.IndexOf('@', StringComparison.Ordinal);
        var name = at > 0 ? uniqueName[..at] : uniqueName;
        var slash = name.LastIndexOf('\\');
        return (slash >= 0 ? name[(slash + 1)..] : name).Replace('.', '-');
    }

    // ── Roles (maintainer rule) ──────────────────────────────────────────────────────────────────

    private async Task<(Dictionary<string, string> Roles, string? Note)> RolesAsync(
        TrackerBinding binding, string tenantId, string pat, List<string> authorIds, CancellationToken ct)
    {
        var roles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (authorIds.Count == 0) { return (roles, null); }
        try
        {
            var (admins, contributors) = await GroupDescriptorsAsync(binding, tenantId, pat, ct).ConfigureAwait(false);
            if (admins is null && contributors is null) { return (roles, RolesUnreadableNote); }
            var url = $"{IdentityBase(binding)}/_apis/identities?identityIds={string.Join(',', authorIds.Take(100).Select(Uri.EscapeDataString))}&queryMembership=Expanded&api-version={ApiVersion}";
            using var response = await SendAsync(HttpMethod.Get, url, pat, null, null, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) { return (roles, RolesUnreadableNote); }
            foreach (var (id, memberOf) in ParseMemberships(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)))
            {
                var role = RoleOf(memberOf, admins, contributors);
                if (role is not null) { roles[id] = role; }
            }
            return (roles, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return (roles, RolesUnreadableNote);
        }
    }

    /// <summary>The association for an identity in <paramref name="memberOf"/> groups: admins first, then contributors.</summary>
    internal static string? RoleOf(IReadOnlyCollection<string> memberOf, string? adminsDescriptor, string? contributorsDescriptor)
    {
        if (adminsDescriptor is not null && memberOf.Contains(adminsDescriptor, StringComparer.OrdinalIgnoreCase)) { return TicketDiscussion.ProjectAdminAssociation; }
        if (contributorsDescriptor is not null && memberOf.Contains(contributorsDescriptor, StringComparer.OrdinalIgnoreCase)) { return TicketDiscussion.ProjectMemberAssociation; }
        return null;
    }

    private async Task<(string? Admins, string? Contributors)> GroupDescriptorsAsync(TrackerBinding binding, string tenantId, string pat, CancellationToken ct)
    {
        var key = $"{tenantId}\n{binding.Host}\n{binding.Site}\n{binding.Project}".ToUpperInvariant();
        if (GroupCache.TryGetValue(key, out var hit) && _clock.GetUtcNow() - hit.At < RolesTtl) { return (hit.Admins, hit.Contributors); }
        var admins = await GroupDescriptorAsync(binding, AdminsGroup, pat, ct).ConfigureAwait(false);
        var contributors = await GroupDescriptorAsync(binding, ContributorsGroup, pat, ct).ConfigureAwait(false);
        if (admins is not null || contributors is not null) { GroupCache[key] = (_clock.GetUtcNow(), admins, contributors); }
        return (admins, contributors);
    }

    private async Task<string?> GroupDescriptorAsync(TrackerBinding binding, string group, string pat, CancellationToken ct)
    {
        var filter = $"[{binding.Project.Trim()}]\\{group}";
        using var response = await SendAsync(HttpMethod.Get,
            $"{IdentityBase(binding)}/_apis/identities?searchFilter=General&filterValue={Uri.EscapeDataString(filter)}&queryMembership=None&api-version={ApiVersion}",
            pat, null, null, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) { return null; }
        return ParseGroupDescriptor(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    /// <summary>Pure parser for an Identities search: the first group's descriptor.</summary>
    internal static string? ParseGroupDescriptor(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array) { return null; }
        foreach (var identity in value.EnumerateArray())
        {
            if (Descriptor(identity.TryGetProperty("descriptor", out var d) ? d : default) is { Length: > 0 } descriptor) { return descriptor; }
        }
        return null;
    }

    /// <summary>Pure parser for an Identities read with <c>queryMembership=Expanded</c>: each identity id and the
    /// descriptors of every group it belongs to (directly or through nesting).</summary>
    internal static IReadOnlyList<(string Id, IReadOnlyCollection<string> MemberOf)> ParseMemberships(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array) { return []; }
        var list = new List<(string, IReadOnlyCollection<string>)>();
        foreach (var identity in value.EnumerateArray())
        {
            if (identity.ValueKind != JsonValueKind.Object || Str(identity, "id") is not { Length: > 0 } id) { continue; }
            var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (identity.TryGetProperty("memberOf", out var memberOf) && memberOf.ValueKind == JsonValueKind.Array)
            {
                groups.UnionWith(memberOf.EnumerateArray().Select(Descriptor).OfType<string>().Where(d => d.Length > 0));
            }
            list.Add((id, groups));
        }
        return list;
    }

    // A descriptor is a string ("Microsoft.TeamFoundation.Identity;S-1-9-…") or an object {identityType, identifier}.
    private static string? Descriptor(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Object when Str(e, "identityType") is { } type && Str(e, "identifier") is { } identifier => $"{type};{identifier}",
        _ => null,
    };

    // ── Write ────────────────────────────────────────────────────────────────────────────────────

    public async Task<bool> CommentAsync(TrackerBinding binding, string pat, int id, string markdown, CancellationToken ct)
    {
        var body = new JsonObject { ["text"] = markdown }.ToJsonString();
        using var response = await SendAsync(HttpMethod.Post,
            $"{ProjectBase(binding)}/_apis/wit/workItems/{id.ToString(CultureInfo.InvariantCulture)}/comments?format=markdown&api-version={CommentsApiVersion}",
            pat, body, "application/json", ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> SetStateAsync(TrackerBinding binding, string pat, int id, string state, CancellationToken ct)
    {
        var patch = new JsonArray(new JsonObject { ["op"] = "add", ["path"] = "/fields/System.State", ["value"] = state }).ToJsonString();
        using var response = await SendAsync(HttpMethod.Patch,
            $"{ProjectBase(binding)}/_apis/wit/workitems/{id.ToString(CultureInfo.InvariantCulture)}?api-version={ApiVersion}",
            pat, patch, "application/json-patch+json", ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> LinkAsync(TrackerBinding binding, string pat, int id, string url, string title, CancellationToken ct)
    {
        var item = $"{ProjectBase(binding)}/_apis/wit/workitems/{id.ToString(CultureInfo.InvariantCulture)}";
        using (var current = await SendAsync(HttpMethod.Get, $"{item}?$expand=relations&api-version={ApiVersion}", pat, null, null, ct).ConfigureAwait(false))
        {
            if (current.IsSuccessStatusCode && HasRelation(await current.Content.ReadAsStringAsync(ct).ConfigureAwait(false), url)) { return true; }
        }
        var patch = new JsonArray(new JsonObject
        {
            ["op"] = "add",
            ["path"] = "/relations/-",
            ["value"] = new JsonObject { ["rel"] = "Hyperlink", ["url"] = url, ["attributes"] = new JsonObject { ["comment"] = title } },
        }).ToJsonString();
        using var response = await SendAsync(HttpMethod.Patch, $"{item}?api-version={ApiVersion}", pat, patch, "application/json-patch+json", ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    /// <summary>Pure check on a work item read with relations: is <paramref name="url"/> already linked?</summary>
    internal static bool HasRelation(string json, string url)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("relations", out var rels) && rels.ValueKind == JsonValueKind.Array
            && rels.EnumerateArray().Any(r => string.Equals(Str(r, "url"), url, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<CreatedTicket> CreateAsync(TrackerBinding binding, string pat, TicketDraft draft, CancellationToken ct)
    {
        var ops = new JsonArray(
            Op("/fields/System.Title", draft.Title),
            Op("/fields/System.Description", TrackerText.TextToHtml(draft.Body)));
        var tags = draft.Labels.Concat(draft.AiReady ? [StandardLabels.AiReady] : []).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (tags.Count > 0) { ops.Add(Op("/fields/System.Tags", string.Join("; ", tags))); }
        // New tickets land where the board reads them.
        if (!string.IsNullOrWhiteSpace(binding.Filter?.AreaPath)) { ops.Add(Op("/fields/System.AreaPath", binding.Filter.AreaPath.Trim())); }
        if (!string.IsNullOrWhiteSpace(binding.Filter?.IterationPath) && !binding.Filter.IterationPath.TrimStart().StartsWith('@'))
        {
            ops.Add(Op("/fields/System.IterationPath", binding.Filter.IterationPath.Trim()));
        }
        using var response = await SendAsync(HttpMethod.Post, $"{ProjectBase(binding)}/_apis/wit/workitems/$Task?api-version={ApiVersion}",
            pat, ops.ToJsonString(), "application/json-patch+json", ct).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Azure DevOps couldn't create the work item ({(int)response.StatusCode}): {ErrorMessage(json) ?? "check the token's Work Items (Read & write) scope"}.");
        }
        using var doc = JsonDocument.Parse(json);
        var id = doc.RootElement.GetProperty("id").GetInt32();
        var idText = id.ToString(CultureInfo.InvariantCulture);
        return new CreatedTicket(id, idText, binding.TicketUrl(id), idText, draft.Title);

        static JsonObject Op(string path, string value) => new() { ["op"] = "add", ["path"] = path, ["value"] = value };
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────────────────────────

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string pat, string? body, string? contentType, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = Basic(pat);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType ?? "application/json") { CharSet = "utf-8" };
        }
        return SendOwnedAsync(request, ct);
    }

    private async Task<HttpResponseMessage> SendOwnedAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using (request)
        {
            return await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
    }

    internal static string? ErrorMessage(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) { return null; }
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? Str(doc.RootElement, "message") : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
