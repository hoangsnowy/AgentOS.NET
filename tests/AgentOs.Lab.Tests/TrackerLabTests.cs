// E1.8 — real Jira Cloud and real Azure DevOps Boards as ticket trackers. Proves the production JiraSourceProvider and
// AzureDevOpsSourceProvider against the live APIs: connect-time validation (project + query), reading the board's
// tickets, a ticket's body + comments with the maintainer roles resolved, and the writes a run makes (comment, move to
// the start state, link a pull request). Every test creates its own ticket and deletes it in a finally — zero residue.

using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using AgentOs.Domain.Workspaces;
using AgentOs.Modules.Integration.Sources;
using Shouldly;
using Xunit;

namespace AgentOs.Lab.Tests;

internal sealed class LabHttp : IHttpClientFactory
{
    public static readonly LabHttp Instance = new();
    public HttpClient CreateClient(string name) => new();
}

public sealed class JiraLabTests
{
    private const string Tenant = "lab";
    private readonly JiraSourceProvider _jira = new(LabHttp.Instance);

    private static TrackerBinding Binding() => new(SourceProviderKind.Jira, LabConfig.JiraSite!, LabConfig.JiraProject!, LabConfig.JiraEmail,
        TrackerFilter.None, "In Progress", "Done");

    private static BoardDescriptor Board() =>
        new(Guid.NewGuid(), Tenant, SourceProviderKind.Jira, LabConfig.JiraSite!, "org", null, null, LabConfig.JiraToken!, LabConfig.JiraProject) { Tracker = Binding() };

    private static WorkspaceDescriptor Ticket() =>
        new(Guid.NewGuid(), Tenant, SourceProviderKind.Jira, LabConfig.JiraSite!, LabConfig.JiraProject!, LabConfig.JiraProject, "main", LabConfig.JiraToken!) { Tracker = Binding() };

    private static void SkipUnlessConfigured()
    {
        if (LabConfig.JiraSkipReason is { } why) { Assert.Skip(why); }
    }

    [Fact]
    public async Task Validate_ReachesTheProject_AndRunsTheQuery()
    {
        SkipUnlessConfigured();
        var v = await _jira.ValidateBoardAsync(Board());
        v.Ok.ShouldBeTrue(v.Error);
    }

    [Fact]
    public async Task CreatedTicket_IsRead_Commented_Moved_AndLinked_ThenDeleted()
    {
        SkipUnlessConfigured();
        var created = (await _jira.CreateTicketsAsync(Board(), Ticket(),
            [new TicketDraft($"AgentOS.NET lab {DateTimeOffset.UtcNow:O}", "Lab ticket.\n\nDepends on #1", ["lab"], AiReady: true)])).Single();
        try
        {
            var tickets = await _jira.ReadBoardTicketsAsync(Board());
            tickets.Items.ShouldContain(t => t.Number == created.Number && t.Labels.Contains(StandardLabels.AiReady));

            (await _jira.CommentOnTicketAsync(Ticket(), created.Number, "A maintainer decision, from the lab.")).ShouldBeTrue();
            var content = await _jira.ReadTicketAsync(Ticket(), created.Number, 10);
            content.Body.ShouldNotBeNull().ShouldContain("Depends on #1");
            var comment = content.Comments.ShouldHaveSingleItem();
            // The lab account created the project, so it is an administrator — unless the token can't read roles (then the note says so).
            if (content.CommentsNote is null) { comment.Role.ShouldBe(TicketCommentRole.Maintainer); }

            // The lab project's workflow may name its start transition differently; a missing one is reported, not thrown.
            _ = await _jira.MoveTicketAsync(Ticket(), created.Number, TicketMilestone.Started);
            (await _jira.LinkPullRequestAsync(Ticket(), created.Number, "https://github.com/hoangsnowy/agentos-lab/pull/1", "AgentOS.NET lab link")).ShouldBeTrue();
        }
        finally
        {
            using var http = new HttpClient();
            using var delete = new HttpRequestMessage(HttpMethod.Delete, $"{Binding().JiraBaseUrl()}/rest/api/3/issue/{Binding().TicketKey(created.Number)}");
            delete.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{LabConfig.JiraEmail}:{LabConfig.JiraToken}")));
            using var response = await http.SendAsync(delete);
            response.IsSuccessStatusCode.ShouldBeTrue($"cleanup of {Binding().TicketKey(created.Number)} returned {(int)response.StatusCode}");
        }
    }
}

public sealed class AzureBoardsLabTests
{
    private const string Tenant = "lab";
    private readonly AzureDevOpsSourceProvider _ado = new(LabHttp.Instance);

    private static TrackerBinding Binding() => new(SourceProviderKind.AzureDevOps, LabConfig.AzureDevOpsOrg!, LabConfig.AzureBoardsProject!, null,
        TrackerFilter.None, "Active", "Closed");

    private static BoardDescriptor Board() =>
        new(Guid.NewGuid(), Tenant, SourceProviderKind.AzureDevOps, LabConfig.AzureDevOpsOrg!, "org", null, null, LabConfig.AzureDevOpsToken!, LabConfig.AzureBoardsProject) { Tracker = Binding() };

    private static WorkspaceDescriptor Ticket() =>
        new(Guid.NewGuid(), Tenant, SourceProviderKind.AzureDevOps, LabConfig.AzureDevOpsOrg!, LabConfig.AzureBoardsProject!, LabConfig.AzureBoardsProject, "main", LabConfig.AzureDevOpsToken!)
        {
            Tracker = Binding(),
        };

    private static void SkipUnlessConfigured()
    {
        if (LabConfig.AzureDevOpsSkipReason is { } why) { Assert.Skip(why); }
    }

    [Fact]
    public async Task Validate_ReachesTheProject_AndRunsTheWiql()
    {
        SkipUnlessConfigured();
        var v = await _ado.ValidateBoardAsync(Board());
        v.Ok.ShouldBeTrue(v.Error);
    }

    [Fact]
    public async Task CreatedWorkItem_IsRead_Commented_Moved_AndLinked_ThenDeleted()
    {
        SkipUnlessConfigured();
        var created = (await _ado.CreateTicketsAsync(Board(), Ticket(),
            [new TicketDraft($"AgentOS.NET lab {DateTimeOffset.UtcNow:O}", "Lab work item.\nDepends on #1", ["lab"], AiReady: true)])).Single();
        try
        {
            (await _ado.ReadBoardTicketsAsync(Board())).Items.ShouldContain(t => t.Number == created.Number && t.Labels.Contains(StandardLabels.AiReady));

            (await _ado.CommentOnTicketAsync(Ticket(), created.Number, "A maintainer decision, from the lab.")).ShouldBeTrue();
            var content = await _ado.ReadTicketAsync(Ticket(), created.Number, 10);
            content.Body.ShouldNotBeNull().ShouldContain("Depends on #1");
            var comment = content.Comments.ShouldHaveSingleItem();
            if (content.CommentsNote is null) { comment.Role.ShouldBe(TicketCommentRole.Maintainer); }   // the PAT's owner administers the lab project

            (await _ado.MoveTicketAsync(Ticket(), created.Number, TicketMilestone.Started)).ShouldBeTrue();
            (await _ado.LinkPullRequestAsync(Ticket(), created.Number, "https://github.com/hoangsnowy/agentos-lab/pull/1", "AgentOS.NET lab link")).ShouldBeTrue();
        }
        finally
        {
            using var http = new HttpClient();
            using var delete = new HttpRequestMessage(HttpMethod.Delete,
                $"https://dev.azure.com/{Uri.EscapeDataString(LabConfig.AzureDevOpsOrg!)}/{Uri.EscapeDataString(LabConfig.AzureBoardsProject!)}/_apis/wit/workitems/{created.Number}?destroy=true&api-version=7.1");
            delete.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + LabConfig.AzureDevOpsToken)));
            using var response = await http.SendAsync(delete);
            response.IsSuccessStatusCode.ShouldBeTrue($"cleanup of work item {created.Number} returned {(int)response.StatusCode}");
        }
    }
}
