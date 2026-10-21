// ADR-0012 stage 1 — the bootstrap script covers every stage-1 tenant table with an enabled + forced policy, uses
// the PG16 membership syntax only where it exists, and the per-connection switch never runs before bootstrap or
// inside a bypass scope. The database-level behaviour is proven on real Postgres by RowLevelSecurityRealDbTests.

using System.Linq;
using AgentOs.SharedKernel.Identity;
using AgentOs.SharedKernel.Persistence;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Persistence;

public sealed class RowLevelSecurityTests
{
    [Fact]
    public void BootstrapSql_PolicesEveryStageOneTable_EnabledAndForced()
    {
        var sql = RowLevelSecurity.BootstrapSql(18);
        foreach (var (schema, table) in RowLevelSecurity.Tables)
        {
            sql.ShouldContain($"ALTER TABLE \"{schema}\".\"{table}\" ENABLE ROW LEVEL SECURITY;");
            sql.ShouldContain($"ALTER TABLE \"{schema}\".\"{table}\" FORCE ROW LEVEL SECURITY;");
            sql.ShouldContain($"CREATE POLICY agentos_tenant ON \"{schema}\".\"{table}\" USING");
        }
        sql.ShouldContain("NOBYPASSRLS");
        sql.ShouldContain("WITH CHECK");
    }

    [Fact]
    public void BootstrapSql_UsesTheMembershipSyntaxTheServerSupports()
    {
        RowLevelSecurity.BootstrapSql(16).ShouldContain("WITH INHERIT FALSE, SET TRUE");
        RowLevelSecurity.BootstrapSql(15).ShouldNotContain("SET TRUE");
    }

    [Fact]
    public void StageOne_LeavesPlatformScopedTablesToStageTwo()
    {
        // app_config and audit_events hold platform-scope rows (__platform / _platform) read by every tenant.
        RowLevelSecurity.Tables.ShouldNotContain(t => t.Table == "app_config" || t.Table == "audit_events");
        RowLevelSecurity.Tables.Count.ShouldBe(13);   // + pipeline.scheduled_runs, memory.nodes, memory.edges, workspaces.autopilot_events
        RowLevelSecurity.Tables.ShouldContain(("pipeline", "scheduled_runs"));
        RowLevelSecurity.Tables.ShouldContain(("memory", "nodes"));
    }

    [Fact]
    public void CurrentTenant_PrefersTheAmbientIdentity_ThenTheHttpRequest()
    {
        var previous = RowLevelSecurity.HttpTenant;
        try
        {
            RowLevelSecurity.HttpTenant = () => "from-http";
            RowLevelSecurity.CurrentTenant().ShouldBe("from-http");
            using (AmbientIdentity.Push("from-run", "u"))
            {
                RowLevelSecurity.CurrentTenant().ShouldBe("from-run");
            }
            RowLevelSecurity.HttpTenant = null;
            RowLevelSecurity.CurrentTenant().ShouldBeNull();
        }
        finally
        {
            RowLevelSecurity.HttpTenant = previous;
        }
    }
}
