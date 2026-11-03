// E1.8 — response fixtures shaped like the real Jira Cloud REST v3 and Azure DevOps REST 7.1 JSON (trimmed to the
// fields the providers read, plus a few they must ignore), and a recording HTTP handler that serves them by route.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Workspaces;

namespace AgentOs.Tests.Integration;

internal static class TrackerFixtures
{
    public static readonly TrackerBinding Jira = new(SourceProviderKind.Jira, "https://contoso.atlassian.net", "SHOP", "bot@contoso.com",
        new TrackerFilter(Jql: "labels = \"ai:ready\""), "In Progress", "Done");

    public static readonly TrackerBinding Ado = new(SourceProviderKind.AzureDevOps, "contoso", "Shop", null,
        new TrackerFilter(AreaPath: "Shop\\Checkout", States: ["New", "Active"], Tags: ["ai:ready"]), "Active", "Closed");

    // ── Jira Cloud REST v3 ──────────────────────────────────────────────────────────────────────

    public const string JiraProject = """
        {"expand":"description,lead","self":"https://contoso.atlassian.net/rest/api/3/project/10000","id":"10000","key":"SHOP","name":"Shop","projectTypeKey":"software","simplified":false}
        """;

    // POST /rest/api/3/search/jql — page 1 (SHOP-12 open + ai:ready, SHOP-11 done, OPS-3 moved in from another project).
    public const string JiraSearchPage1 = """
        {"issues":[
          {"expand":"","id":"10012","self":"https://contoso.atlassian.net/rest/api/3/issue/10012","key":"SHOP-12",
           "fields":{"summary":"Add a coupon field to checkout","labels":["ai:ready","repo:shop-web","p1"],
             "status":{"self":"…","name":"To Do","id":"10000","statusCategory":{"id":2,"key":"new","name":"To Do","colorName":"blue-gray"}},
             "assignee":{"accountId":"5b10ac8d82e05b22cc7d4ef5","displayName":"Mia Krystof","active":true},
             "issuetype":{"id":"10001","name":"Story","subtask":false}}},
          {"id":"10011","key":"SHOP-11",
           "fields":{"summary":"Checkout totals","labels":[],
             "status":{"name":"Done","statusCategory":{"id":3,"key":"done","name":"Done"}},
             "assignee":null,"issuetype":{"name":"Bug"}}},
          {"id":"20003","key":"OPS-3","fields":{"summary":"Moved here by mistake","labels":["ai:ready"],"status":{"name":"To Do","statusCategory":{"key":"new"}}}}
        ],"nextPageToken":"CAEaAggD","isLast":false}
        """;

    public const string JiraSearchPage2 = """
        {"issues":[
          {"id":"10013","key":"SHOP-13","fields":{"summary":"Coupon API","labels":["ai:ready"],
            "status":{"name":"In Progress","statusCategory":{"key":"indeterminate"}},"issuetype":{"name":"Task"}}}
        ],"isLast":true}
        """;

    // GET /rest/api/3/issue/SHOP-12?fields=description — ADF.
    public const string JiraIssue = """
        {"id":"10012","key":"SHOP-12","fields":{"description":{"type":"doc","version":1,"content":[
          {"type":"paragraph","content":[{"type":"text","text":"Customers can enter a coupon code."},{"type":"hardBreak"},{"type":"text","text":"Depends on #11"}]},
          {"type":"heading","attrs":{"level":3},"content":[{"type":"text","text":"Acceptance criteria"}]},
          {"type":"bulletList","content":[
            {"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"Invalid codes show an error"}]}]},
            {"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"Ask "},{"type":"mention","attrs":{"id":"5b10","text":"@Mia Krystof"}}]}]}
          ]}
        ]}}}
        """;

    // GET /rest/api/3/issue/SHOP-12/comment?orderBy=-created — newest first.
    public const string JiraComments = """
        {"startAt":0,"maxResults":60,"total":4,"comments":[
          {"id":"10404","author":{"accountId":"app-1","displayName":"Automation for Jira","accountType":"app"},
           "body":{"type":"doc","version":1,"content":[{"type":"paragraph","content":[{"type":"text","text":"Status changed."}]}]},
           "created":"2026-11-02T10:00:00.000+0000"},
          {"id":"10403","author":{"accountId":"acc-outsider","displayName":"Sam Outside","accountType":"atlassian"},
           "body":{"type":"doc","version":1,"content":[{"type":"paragraph","content":[{"type":"text","text":"Please also add gift cards!"}]}]},
           "created":"2026-11-02T09:00:00.000+0000"},
          {"id":"10402","author":{"accountId":"acc-dev","displayName":"Dev Member","accountType":"atlassian"},
           "body":{"type":"doc","version":1,"content":[{"type":"paragraph","content":[{"type":"text","text":"Codes are case-insensitive."}]}]},
           "created":"2026-11-01T12:00:00.000+0000"},
          {"id":"10401","author":{"accountId":"acc-admin","displayName":"Ada Admin","accountType":"atlassian"},
           "body":{"type":"doc","version":1,"content":[{"type":"paragraph","content":[{"type":"text","text":"Max one coupon per order."}]}]},
           "created":"2026-11-01T08:00:00.000+0000"}
        ]}
        """;

    public const string JiraRoles = """
        {"Administrators":"https://contoso.atlassian.net/rest/api/3/project/10000/role/10002",
         "Developers":"https://contoso.atlassian.net/rest/api/3/project/10000/role/10001",
         "Viewers":"https://contoso.atlassian.net/rest/api/3/project/10000/role/10003"}
        """;

    public const string JiraAdminRole = """
        {"self":"…","name":"Administrators","id":10002,"actors":[
          {"id":1,"displayName":"Ada Admin","type":"atlassian-user-role-actor","actorUser":{"accountId":"acc-admin"}}]}
        """;

    public const string JiraDeveloperRole = """
        {"self":"…","name":"Developers","id":10001,"actors":[
          {"id":2,"displayName":"shop-devs","type":"atlassian-group-role-actor","name":"shop-devs","actorGroup":{"name":"shop-devs","displayName":"shop-devs","groupId":"g-42"}}]}
        """;

    public const string JiraGroupMembers = """
        {"self":"…","maxResults":200,"startAt":0,"total":1,"isLast":true,"values":[{"accountId":"acc-dev","displayName":"Dev Member","active":true}]}
        """;

    public const string JiraTransitions = """
        {"expand":"transitions","transitions":[
          {"id":"11","name":"To Do","to":{"name":"To Do","id":"10000"}},
          {"id":"21","name":"Start work","to":{"name":"In Progress","id":"3"}},
          {"id":"31","name":"Done","to":{"name":"Done","id":"10001"}}]}
        """;

    public const string JiraCreated = """{"id":"10020","key":"SHOP-20","self":"https://contoso.atlassian.net/rest/api/3/issue/10020"}""";

    // ── Azure DevOps REST 7.1 ───────────────────────────────────────────────────────────────────

    public const string AdoProject = """
        {"id":"eb6e4656-77fc-42a1-9181-4c6d8e9da5d1","name":"Shop","url":"https://dev.azure.com/contoso/_apis/projects/eb6e4656","state":"wellFormed","visibility":"private"}
        """;

    public const string AdoWiql = """
        {"queryType":"flat","queryResultType":"workItem","asOf":"2026-11-03T10:00:00Z",
         "columns":[{"referenceName":"System.Id","name":"ID"}],
         "workItems":[{"id":42,"url":"https://dev.azure.com/contoso/_apis/wit/workItems/42"},{"id":41,"url":"…"}]}
        """;

    public const string AdoWorkItems = """
        {"count":2,"value":[
          {"id":41,"rev":3,"fields":{"System.Id":41,"System.WorkItemType":"Bug","System.State":"Closed","System.Title":"Totals rounding"},"url":"…"},
          {"id":42,"rev":7,"fields":{"System.Id":42,"System.WorkItemType":"User Story","System.State":"New","System.Title":"Coupon field",
            "System.Tags":"ai:ready; repo:shop-web; p0",
            "System.AssignedTo":{"displayName":"Mia Krystof","uniqueName":"mia@contoso.com","id":"d2b1"}},"url":"…"}
        ]}
        """;

    public const string AdoWorkItem = """
        {"id":42,"rev":7,"fields":{
          "System.Description":"<div>Customers can enter a coupon&nbsp;code.<br>Depends on #41</div><ul><li>One per order</li></ul>",
          "Microsoft.VSTS.Common.AcceptanceCriteria":"<p>Invalid codes show an error</p>"}}
        """;

    public const string AdoComments = """
        {"totalCount":3,"count":3,"comments":[
          {"workItemId":42,"commentId":3,"version":1,"text":"Please also add gift cards!","format":"markdown",
           "createdBy":{"displayName":"Sam Outside","uniqueName":"sam@outside.com","id":"id-outsider","descriptor":"aad.AAA"},"createdDate":"2026-11-02T09:00:00Z","isDeleted":false},
          {"workItemId":42,"commentId":2,"version":1,"text":"<div>Codes are <b>case-insensitive</b>.</div>",
           "createdBy":{"displayName":"Dev Member","uniqueName":"dev@contoso.com","id":"id-dev"},"createdDate":"2026-11-01T12:00:00Z","isDeleted":false},
          {"workItemId":42,"commentId":1,"version":1,"text":"Max one coupon per order.","format":"markdown",
           "createdBy":{"displayName":"Ada Admin","uniqueName":"ada@contoso.com","id":"id-admin"},"createdDate":"2026-11-01T08:00:00Z","isDeleted":false}
        ]}
        """;

    public const string AdoAdminsGroup = """
        {"count":1,"value":[{"id":"g-admins","descriptor":"Microsoft.TeamFoundation.Identity;S-1-9-1551374245-1204400969-2402986413-2179408616-0-0-0-0-1","providerDisplayName":"[Shop]\\Project Administrators","isActive":true}]}
        """;

    public const string AdoContributorsGroup = """
        {"count":1,"value":[{"id":"g-contrib","descriptor":"Microsoft.TeamFoundation.Identity;S-1-9-1551374245-1204400969-2402986413-2179408616-0-0-0-0-2","providerDisplayName":"[Shop]\\Contributors","isActive":true}]}
        """;

    // Memberships: the admin by string descriptor, the dev through the object form, the outsider in neither.
    public const string AdoMemberships = """
        {"count":3,"value":[
          {"id":"id-admin","descriptor":"aad.ADMIN","memberOf":["Microsoft.TeamFoundation.Identity;S-1-9-1551374245-1204400969-2402986413-2179408616-0-0-0-0-1","Microsoft.TeamFoundation.Identity;S-1-9-9"]},
          {"id":"id-dev","memberOf":[{"identityType":"Microsoft.TeamFoundation.Identity","identifier":"S-1-9-1551374245-1204400969-2402986413-2179408616-0-0-0-0-2"}]},
          {"id":"id-outsider","memberOf":["Microsoft.TeamFoundation.Identity;S-1-9-1551374245-1204400969-2402986413-2179408616-0-0-0-0-3"]}
        ]}
        """;

    public const string AdoRelations = """
        {"id":42,"relations":[{"rel":"Hyperlink","url":"https://github.com/octo/shop-web/pull/7","attributes":{"comment":"feat: Coupon field (AB#42)"}}]}
        """;
}

/// <summary>A recorded request: method, URL, Authorization header and body.</summary>
internal sealed record RecordedRequest(HttpMethod Method, Uri Url, string? Authorization, string? ContentType, string? Body);

/// <summary>Serves canned responses by (method, URL substring), first match wins; records every request. Unmatched → 404.</summary>
internal sealed class RecordingHandler : HttpMessageHandler
{
    private readonly List<(HttpMethod? Method, string UrlPart, string? BodyPart, HttpStatusCode Status, string Body)> _routes = [];

    public List<RecordedRequest> Requests { get; } = [];

    public RecordingHandler On(HttpMethod? method, string urlPart, string body, HttpStatusCode status = HttpStatusCode.OK, string? whenBodyContains = null)
    {
        _routes.Add((method, urlPart, whenBodyContains, status, body));
        return this;
    }

    public IEnumerable<RecordedRequest> To(HttpMethod method, string urlPart) =>
        Requests.Where(r => r.Method == method && r.Url.ToString().Contains(urlPart, StringComparison.Ordinal));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var url = request.RequestUri!;
        Requests.Add(new RecordedRequest(request.Method, url, request.Headers.Authorization?.ToString(),
            request.Content?.Headers.ContentType?.MediaType, body));
        var route = _routes.FirstOrDefault(r => Matches(r.Method, r.UrlPart, r.BodyPart));
        return route.UrlPart is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") }
            : new HttpResponseMessage(route.Status) { Content = new StringContent(route.Body, Encoding.UTF8, "application/json") };

        bool Matches(HttpMethod? method, string urlPart, string? bodyPart) =>
            MethodMatches(method, request.Method) && url.ToString().Contains(urlPart, StringComparison.Ordinal) && BodyMatches(bodyPart, body);
    }

    private static bool MethodMatches(HttpMethod? wanted, HttpMethod actual) => wanted is null || wanted == actual;

    private static bool BodyMatches(string? wanted, string? body) =>
        wanted is null || (body?.Contains(wanted, StringComparison.Ordinal) ?? false);
}

/// <summary>An <see cref="IHttpClientFactory"/> handing out clients over one handler.</summary>
internal sealed class HandlerHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}
