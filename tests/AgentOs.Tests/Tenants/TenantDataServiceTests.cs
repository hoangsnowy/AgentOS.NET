// E1.4 — purge deletes children before parents (foreign-key order, Kahn's algorithm) so one transaction removes
// every tenant row; self-references and cycles don't hang it. Export/purge against real Postgres is proven by
// TenantDataRealAuthTests.

using System.Linq;
using AgentOs.Modules.Tenants.Data;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Tenants;

public sealed class TenantDataServiceTests
{
    private static TenantDataService.TenantTable T(uint oid, string name) => new(oid, name.Split('.')[0], name.Split('.')[1]);

    [Fact]
    public void DeleteOrder_PutsChildrenBeforeParents()
    {
        var runs = T(1, "pipeline.pipeline_runs");
        var metrics = T(2, "pipeline.run_metrics");          // FK → pipeline_runs
        var sessions = T(3, "sessions.sessions");
        var repos = T(4, "sessions.session_repos");           // FK → sessions
        var order = TenantDataService.DeleteOrder([runs, metrics, sessions, repos], [(2, 1), (4, 3)]).Select(t => t.Name).ToList();

        order.IndexOf("pipeline.run_metrics").ShouldBeLessThan(order.IndexOf("pipeline.pipeline_runs"));
        order.IndexOf("sessions.session_repos").ShouldBeLessThan(order.IndexOf("sessions.sessions"));
        order.Count.ShouldBe(4);
    }

    [Fact]
    public void DeleteOrder_IgnoresSelfReferences_AndForeignTables_AndSurvivesCycles()
    {
        var a = T(1, "x.a");
        var b = T(2, "x.b");
        var order = TenantDataService.DeleteOrder([a, b], [(1, 1), (1, 99), (1, 2), (2, 1)]);
        order.Count.ShouldBe(2);   // the a↔b cycle is appended, not dropped or looped on
    }

    [Fact]
    public void Quoted_EscapesIdentifiers()
        => new TenantDataService.TenantTable(1, "we\"ird", "t").Quoted.ShouldBe("\"we\"\"ird\".\"t\"");
}
