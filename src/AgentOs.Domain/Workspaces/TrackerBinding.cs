// E1.8 — an external ticket TRACKER on a board. A board's code still lives in GitHub or Azure Repos (WorkspaceEntity.Kind,
// its token, its repos, its pull requests); a tracker binding says the board's tickets come from somewhere else:
// Azure DevOps Boards (work items) or Jira Cloud (issues). The binding is plain coordinates + a filter + the workflow
// states to move a ticket to; its token lives encrypted in AppConfig (TokenKey) and is never part of the binding.
//
// Ticket numbers stay ints end to end (sessions, dependencies, "#n" in the feed): an Azure DevOps work item id, or the
// number part of a Jira key in the bound project (PROJ-123 → 123, its key rebuilt as Project-Number). One Jira project
// per board keeps the numbers unique and the keys readable.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace AgentOs.Domain.Workspaces;

/// <summary>What a ticket has reached, so its tracker can follow (a workflow transition).</summary>
public enum TicketMilestone
{
    /// <summary>An agent started working on it.</summary>
    Started,

    /// <summary>Its pull request was merged.</summary>
    Merged,
}

/// <summary>
/// Which work items an Azure DevOps tracker reads (every part optional; all set parts must match) — or, for Jira, an
/// extra JQL clause ANDed with the bound project.
/// </summary>
/// <param name="AreaPath">ADO: <c>System.AreaPath UNDER</c> this path.</param>
/// <param name="IterationPath">ADO: <c>System.IterationPath UNDER</c> this path (<c>@CurrentIteration</c> allowed).</param>
/// <param name="States">ADO: <c>System.State IN</c> these states.</param>
/// <param name="Tags">ADO: every tag must be on the work item.</param>
/// <param name="Jql">Jira: extra JQL, e.g. <c>labels = "ai:ready" AND sprint in openSprints()</c>.</param>
public sealed record TrackerFilter(
    string? AreaPath = null,
    string? IterationPath = null,
    IReadOnlyList<string>? States = null,
    IReadOnlyList<string>? Tags = null,
    string? Jql = null)
{
    /// <summary>No filter: every open ticket of the project.</summary>
    public static TrackerFilter None { get; } = new();
}

/// <summary>
/// A board's external tracker. <see cref="Site"/> is the Azure DevOps organization or the Jira Cloud site URL;
/// <see cref="Project"/> the ADO project or the Jira project key.
/// </summary>
/// <param name="Kind"><see cref="SourceProviderKind.AzureDevOps"/> or <see cref="SourceProviderKind.Jira"/>.</param>
/// <param name="Site">ADO organization (<c>contoso</c>) or Jira site (<c>https://contoso.atlassian.net</c>).</param>
/// <param name="Project">ADO project name or Jira project key (<c>SHOP</c>).</param>
/// <param name="Account">Jira: the e-mail the API token belongs to (Basic auth user). Unused by ADO.</param>
/// <param name="Filter">Which tickets the board reads.</param>
/// <param name="StartState">The state (ADO) or transition / target status (Jira) when work starts; empty = don't move.</param>
/// <param name="DoneState">The state / transition when the pull request is merged; empty = don't move.</param>
/// <param name="Host">ADO host for Azure DevOps Server (null = <c>dev.azure.com</c>). Unused by Jira (the site is the host).</param>
public sealed record TrackerBinding(
    SourceProviderKind Kind,
    string Site,
    string Project,
    string? Account,
    TrackerFilter Filter,
    string? StartState,
    string? DoneState,
    string? Host = null)
{
    /// <summary>The trackers a binding can name.</summary>
    public static bool IsTrackerKind(SourceProviderKind kind) => kind is SourceProviderKind.AzureDevOps or SourceProviderKind.Jira;

    /// <summary>Where a board's tracker token is stored (encrypted AppConfig key) — never on the row.</summary>
    public static string TokenKey(Guid boardId) =>
        string.Create(CultureInfo.InvariantCulture, $"workspace/{boardId:N}/tracker-token");

    /// <summary>Default states for a new binding: ADO Agile (<c>Active</c> → <c>Closed</c>), Jira (<c>In Progress</c> → <c>Done</c>).</summary>
    public static (string Start, string Done) DefaultStates(SourceProviderKind kind) =>
        kind == SourceProviderKind.Jira ? ("In Progress", "Done") : ("Active", "Closed");

    /// <summary>The Jira site as an absolute https base URL without a trailing slash (null when it isn't one).</summary>
    public string? JiraBaseUrl()
    {
        var site = Site.Trim();
        if (!site.Contains("://", StringComparison.Ordinal)) { site = "https://" + site; }
        return Uri.TryCreate(site, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Host.Length > 0
            ? uri.GetLeftPart(UriPartial.Authority)
            : null;
    }

    /// <summary>How people write ticket <paramref name="number"/>: <c>SHOP-12</c> (Jira) or <c>AB#12</c> (ADO, the
    /// mention Azure Boards links from a GitHub pull request).</summary>
    public string TicketKey(int number) => Kind == SourceProviderKind.Jira
        ? string.Create(CultureInfo.InvariantCulture, $"{Project.Trim().ToUpperInvariant()}-{number}")
        : string.Create(CultureInfo.InvariantCulture, $"AB#{number}");

    /// <summary>The browser URL of ticket <paramref name="number"/>.</summary>
    public string TicketUrl(int number) => Kind == SourceProviderKind.Jira
        ? $"{JiraBaseUrl() ?? Site.TrimEnd('/')}/browse/{TicketKey(number)}"
        : string.Create(CultureInfo.InvariantCulture,
            $"https://{Host ?? "dev.azure.com"}/{Uri.EscapeDataString(Site.Trim())}/{Uri.EscapeDataString(Project.Trim())}/_workitems/edit/{number}");

    /// <summary>The number of a Jira key in this binding's project (<c>SHOP-12</c> → 12); null for another project's key.</summary>
    public int? NumberOfKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) { return null; }
        var dash = key.LastIndexOf('-');
        if (dash <= 0 || !key.AsSpan(0, dash).Equals(Project.Trim(), StringComparison.OrdinalIgnoreCase)) { return null; }
        return int.TryParse(key.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;
    }

    /// <summary>Validates the coordinates. Null when valid, else a user-facing reason.</summary>
    public string? Problem()
    {
        if (!IsTrackerKind(Kind)) { return "The ticket tracker must be Azure DevOps Boards or Jira."; }
        if (string.IsNullOrWhiteSpace(Site))
        {
            return Kind == SourceProviderKind.Jira ? "The Jira site is required (https://<you>.atlassian.net)." : "The Azure DevOps organization is required.";
        }
        if (string.IsNullOrWhiteSpace(Project))
        {
            return Kind == SourceProviderKind.Jira ? "The Jira project key is required." : "The Azure DevOps project is required.";
        }
        if (Kind == SourceProviderKind.Jira)
        {
            if (JiraBaseUrl() is null) { return "The Jira site must be an https URL, like https://contoso.atlassian.net."; }
            if (string.IsNullOrWhiteSpace(Account)) { return "The e-mail of the Jira account the API token belongs to is required."; }
            if (!Project.Trim().All(c => char.IsAsciiLetterOrDigit(c) || c == '_')) { return "A Jira project key is letters, digits and '_' only."; }
        }
        else if (Site.Trim().Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
        {
            return "The Azure DevOps organization is its name (the part after dev.azure.com/), not a URL.";
        }
        return null;
    }

    /// <summary>The binding as stored on the board row (JSON, source-generated).</summary>
    public string Serialize() => JsonSerializer.Serialize(this, TrackerJsonContext.Default.TrackerBinding);

    /// <summary>The binding stored on a board row; null when there is none (or it can't be read).</summary>
    public static TrackerBinding? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) { return null; }
        try
        {
            var binding = JsonSerializer.Deserialize(json, TrackerJsonContext.Default.TrackerBinding);
            return binding is not null && IsTrackerKind(binding.Kind) ? binding with { Filter = binding.Filter ?? TrackerFilter.None } : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>A board's tracker with its token, resolved just-in-time. The token is transient: never logged or persisted.</summary>
/// <param name="Binding">The tracker coordinates.</param>
/// <param name="AccessToken">The tracker's PAT / API token.</param>
public sealed record TrackerConnection(TrackerBinding Binding, string AccessToken);

/// <summary>Finds a board's external tracker (implemented by the Workspaces module). Tenant-explicit: background runs
/// have no request context.</summary>
public interface IBoardTrackers
{
    /// <summary>Board <paramref name="boardId"/>'s tracker and its token (empty when it went missing — the tracker then
    /// refuses the call); null when the board's tickets come from its code host (GitHub Projects) or it is unknown.</summary>
    Task<TrackerConnection?> GetAsync(string tenantId, Guid boardId, CancellationToken ct = default);
}

/// <summary>Which repository a tracker ticket's code lives in — a tracker knows nothing about repositories.</summary>
public static class TrackerRepoMap
{
    /// <summary>The label / tag prefix that names a ticket's repository: <c>repo:api-service</c> or <c>repo:octo/api-service</c>.</summary>
    public const string RepoLabelPrefix = "repo:";

    /// <summary>
    /// Assigns each ticket a repository of the board (<paramref name="repoKeys"/>, <c>owner/repo</c>, in board order): the
    /// one its <c>repo:</c> label names, else the board's first. A ticket naming a repository the board doesn't have keeps
    /// none (Autopilot skips it rather than build it in the wrong place). Tickets already carrying a repository are kept.
    /// </summary>
    public static IReadOnlyList<BoardTicket> Assign(IReadOnlyList<BoardTicket> tickets, IReadOnlyList<string> repoKeys)
    {
        ArgumentNullException.ThrowIfNull(tickets);
        ArgumentNullException.ThrowIfNull(repoKeys);
        var repos = repoKeys.Select(k => k.Split('/', 2)).Where(p => p.Length == 2 && p[0].Length > 0 && p[1].Length > 0).ToList();
        if (repos.Count == 0) { return tickets; }
        var result = new BoardTicket[tickets.Count];
        for (var i = 0; i < tickets.Count; i++)
        {
            var t = tickets[i];
            if (t.RepoOwner is not null && t.RepoName is not null) { result[i] = t; continue; }
            var named = t.Labels.FirstOrDefault(l => l.StartsWith(RepoLabelPrefix, StringComparison.OrdinalIgnoreCase))?[RepoLabelPrefix.Length..].Trim();
            string[]? repo = named is null ? repos[0] : repos.FirstOrDefault(r => Matches(r, named));
            result[i] = repo is null ? t : t with { RepoOwner = repo[0], RepoName = repo[1] };
        }
        return result;

        static bool Matches(string[] repo, string named) =>
            named.Contains('/', StringComparison.Ordinal)
                ? string.Equals($"{repo[0]}/{repo[1]}", named, StringComparison.OrdinalIgnoreCase)
                : string.Equals(repo[1], named, StringComparison.OrdinalIgnoreCase);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TrackerBinding))]
internal sealed partial class TrackerJsonContext : JsonSerializerContext;
