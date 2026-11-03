// REAL-POSTGRES — RLS stage 2 with the process-wide switch ON (RowLevelSecurity.State = Enabled), through the real
// module code: the paths that run with NO tenant of their own keep working, and none of them crosses tenants.
//   • explicit platform scopes: the autopilot board scan, the scheduler's due-run claim, the runner handshake lookup and
//     the revalidation sweep each see every tenant's rows;
//   • tenant-explicit Dapper reads (workspaces, runners, orchestrations): a flow with no tenant is bound to the tenant it
//     names; a flow bound to tenant B sees none of tenant A's rows even when it names A;
//   • the evidence drain (a background loop, no tenant): a row is written under its own tenant;
//   • config.app_config: a flow bound to B reads the shared platform rows but not A's secrets, and cannot write a
//     platform row outside a platform scope;
//   • the hub binds the rest of the handshake to the runner's tenant — without it, "no tenant" pairs nothing.
// The state is process-wide, so this class runs in a non-parallel collection and restores it.
//   Gate: RUN_AGENTOS_E2E_REAL=true and AGENTOS_PG_CONN (a server the test may CREATE / DROP DATABASE on). Zero residue.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentOs.Domain.Tools;
using AgentOs.Modules.AppConfig;
using AgentOs.Modules.AppConfig.Persistence;
using AgentOs.Modules.Pipeline.Persistence;
using AgentOs.Modules.Pipeline.Persistence.Repositories;
using AgentOs.Modules.Pipeline.Scheduling;
using AgentOs.Modules.Sessions.Persistence;
using AgentOs.Modules.Sessions.Persistence.Repositories;
using AgentOs.Modules.Tools.Evidence;
using AgentOs.Modules.Tools.Persistence;
using AgentOs.Modules.Workspaces.Persistence;
using AgentOs.Modules.Workspaces.Persistence.Repositories;
using AgentOs.SharedKernel.Identity;
using AgentOs.SharedKernel.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Shouldly;
using Xunit;
using Db = AgentOs.Tests.Persistence.RowLevelSecurityStageTwoRealPostgresTests;

namespace AgentOs.Tests.Persistence;

/// <summary>Tests that flip the process-wide <see cref="RowLevelSecurity.State"/> run alone, after the parallel ones.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RowLevelSecurityProcessStateGroup
{
    /// <summary>The collection name.</summary>
    public const string Name = "rls-process-state";
}

[Collection(RowLevelSecurityProcessStateGroup.Name)]
public sealed class RowLevelSecurityEnabledPathsRealPostgresTests
{
    private const string A = "enabled-tenant-a";
    private const string B = "enabled-tenant-b";

    [Fact]
    public async Task EnabledHost_TenantlessPaths_KeepWorking_AndNeverCrossTenants()
    {
        var server = Db.ServerConnection ?? string.Empty;
        if (server.Length == 0) { Assert.Skip("RUN_AGENTOS_E2E_REAL + AGENTOS_PG_CONN required."); }

        var database = "agentos_rlson_" + Guid.NewGuid().ToString("N")[..12];
        var admin = new NpgsqlConnectionStringBuilder(server) { Database = "postgres", Pooling = false }.ConnectionString;
        var cs = new NpgsqlConnectionStringBuilder(server) { Database = database }.ConnectionString;
        await Db.ExecAsync(admin, $"CREATE DATABASE \"{database}\"");
        var previousState = RowLevelSecurity.State;
        var previousHttp = RowLevelSecurity.HttpTenant;
        try
        {
            await Db.MigrateEveryModuleAsync(cs);
            var (runnerA, runnerB) = await SeedAsync(cs);
            DapperConfig.EnsureConfigured();
            var conn = new NpgsqlConnectionFactory(cs);
            var none = new Db.FixedTenant(string.Empty);

            RowLevelSecurity.HttpTenant = null;
            RowLevelSecurity.SetState(RowLevelSecurityState.Enabled);

            // 1. Cross-tenant by nature → explicit platform scopes inside the stores (the flow itself has no tenant).
            await using (var db = new WorkspacesDbContext(Bound<WorkspacesDbContext>(cs, "workspaces")))
            {
                (await new WorkspaceRepository(db, none, conn).ListAutopilotAsync()).Select(w => w.TenantId).ShouldBe([A, B], ignoreOrder: true);
            }
            await using (var db = new PipelineDbContext(Bound<PipelineDbContext>(cs, "pipeline"), none))
            {
                (await new EfScheduledRunStore(db).ClaimDueAsync(DateTimeOffset.UtcNow, 10)).Select(d => d.TenantId).ShouldBe([A, B], ignoreOrder: true);
            }
            await using (var db = new SessionsDbContext(Bound<SessionsDbContext>(cs, "sessions")))
            {
                var directory = new EfRunnerDirectory(db);
                (await directory.FindForPairingAsync(runnerB))!.TenantId.ShouldBe(B);
                (await directory.FindStatusesAsync([runnerA, runnerB])).Count.ShouldBe(2);

                // The handshake's writes: bound to the verified runner's tenant they land; with no tenant they touch nothing.
                (await directory.MarkPairedAsync(runnerA)).ShouldBeFalse();
                using (RowLevelSecurity.TenantScope(A))
                {
                    (await directory.MarkPairedAsync(runnerA)).ShouldBeTrue();
                }
            }

            // 2. Tenant-explicit Dapper reads: an unbound flow is bound to the named tenant; a B flow never sees A's rows.
            await using (var db = new WorkspacesDbContext(Bound<WorkspacesDbContext>(cs, "workspaces")))
            await using (var sessions = new SessionsDbContext(Bound<SessionsDbContext>(cs, "sessions")))
            await using (var pipeline = new PipelineDbContext(Bound<PipelineDbContext>(cs, "pipeline"), none))
            {
                var workspaces = new WorkspaceRepository(db, none, conn);
                var runners = new RunnerRepository(sessions, none, conn);
                var orchestrations = new OrchestrationRepository(pipeline, none, conn);
                (await workspaces.ListForTenantAsync(A)).ShouldHaveSingleItem().TenantId.ShouldBe(A);
                (await runners.ListForTenantAsync(A)).ShouldHaveSingleItem().TenantId.ShouldBe(A);
                (await orchestrations.ListForTenantAsync(A)).ShouldHaveSingleItem();
                using (AmbientIdentity.Push(B, "u"))
                {
                    (await workspaces.ListForTenantAsync(A)).ShouldBeEmpty();
                    (await runners.ListForTenantAsync(A)).ShouldBeEmpty();
                    (await orchestrations.ListForTenantAsync(A)).ShouldBeEmpty();
                    (await workspaces.ListForTenantAsync(B)).ShouldHaveSingleItem();
                }
            }

            // 3. The evidence drain writes from a loop with no tenant: each row lands under its own.
            await using (var tools = Provider(services => services.AddDbContext<ToolsDbContext>(o => o
                .UseNpgsql(cs, n => n.MigrationsHistoryTable("__EFMigrationsHistory", "tools"))
                .AddInterceptors(RowLevelSecurityInterceptor.Instance))))
            {
                var log = new EfToolInvocationLog(tools, NullLogger<EfToolInvocationLog>.Instance);
                var at = DateTimeOffset.UtcNow;
                await log.AppendAsync(new ToolInvocationEvidence("call-1", "build_verifier", A, null, "{}", "ok", false, at, at));
                (await CountAsSuperuserAsync(cs, "tools.tool_invocations", A)).ShouldBe(1L);
            }

            // 4. app_config: shared platform rows, bound tenant rows.
            await using (var settings = Provider(services =>
            {
                services.AddDbContext<AppConfigDbContext>(o => o
                    .UseNpgsql(cs, n => n.MigrationsHistoryTable("__EFMigrationsHistory", "config"))
                    .AddInterceptors(RowLevelSecurityInterceptor.Instance));
                services.AddSingleton<ITenantContext>(none);
                services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
                services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            }))
            {
                // A fresh store (empty per-process cache) per step, so every read below reaches the database.
                EfAppConfigStore Store() => new(settings, settings.GetRequiredService<IDataProtectionProvider>(), NullLogger<EfAppConfigStore>.Instance);
                using (RowLevelSecurity.PlatformScope("test-seed"))
                {
                    var seeder = Store();
                    await seeder.SetForTenantAsync(A, "Llm:Claude:ApiKey", "sk-a");
                    await seeder.SetForTenantAsync(RowLevelSecurity.PlatformSettingsTenant, "@platform/Tenants:OpenSignup", "false");
                }
                (await Store().GetForTenantAsync(A, "Llm:Claude:ApiKey")).ShouldBe("sk-a");   // an unbound flow names A
                using (AmbientIdentity.Push(B, "u"))
                {
                    (await Store().GetForTenantAsync(A, "Llm:Claude:ApiKey")).ShouldBeNull();   // B never reads A's secret
                    (await Store().GetForTenantAsync(RowLevelSecurity.PlatformSettingsTenant, "@platform/Tenants:OpenSignup")).ShouldBe("false");
                    (await Should.ThrowAsync<Exception>(async () =>
                        await Store().SetForTenantAsync(RowLevelSecurity.PlatformSettingsTenant, "@platform/Tenants:OpenSignup", "true")))
                        .ShouldBeAssignableTo<DbUpdateException>();
                    (await Should.ThrowAsync<Exception>(async () => await Store().SetForTenantAsync(A, "Llm:Claude:ApiKey", "stolen")))
                        .ShouldBeAssignableTo<DbUpdateException>();
                }
                (await CountAsSuperuserAsync(cs, "config.app_config", A)).ShouldBe(1L);
            }
        }
        finally
        {
            RowLevelSecurity.SetState(previousState);
            RowLevelSecurity.HttpTenant = previousHttp;
            NpgsqlConnection.ClearAllPools();
            await Db.ExecAsync(admin, $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)");
        }
    }

    // Module contexts as the modules register them: the RLS interceptor on every connection.
    private static DbContextOptions<T> Bound<T>(string cs, string schema) where T : DbContext
        => new DbContextOptionsBuilder<T>()
            .UseNpgsql(cs, n => n.MigrationsHistoryTable("__EFMigrationsHistory", schema))
            .AddInterceptors(RowLevelSecurityInterceptor.Instance)
            .Options;

    private static ServiceProvider Provider(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        configure(services);
        return services.BuildServiceProvider();
    }

    // One autopilot board, one paired runner, one due schedule and one orchestration per tenant — as the superuser.
    private static async Task<(Guid RunnerA, Guid RunnerB)> SeedAsync(string cs)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await Db.ExecAsync(conn, RowLevelSecurity.BootstrapSql(conn.PostgreSqlVersion.Major));
        var columns = await Db.RequiredColumnsAsync(conn);
        var seed = 0;
        await Db.ExecAsync(conn, "SET session_replication_role = replica");
        foreach (var table in new[] { "workspaces.workspaces", "sessions.runners", "pipeline.scheduled_runs", "pipeline.orchestrations" })
        {
            await Db.InsertAsync(conn, table, columns[table], A, ++seed);
            await Db.InsertAsync(conn, table, columns[table], B, ++seed);
        }
        await Db.ExecAsync(conn, """
            RESET session_replication_role;
            UPDATE workspaces.workspaces SET "Autopilot" = 'Build';
            UPDATE sessions.runners SET "Status" = 'Pending';
            UPDATE pipeline.scheduled_runs SET "Enabled" = true, "Kind" = 'Once', "RunAtUtc" = now() - interval '2 minutes',
                "NextRunAtUtc" = now() - interval '1 minute';
            """);
        return (await RunnerIdAsync(conn, A), await RunnerIdAsync(conn, B));
    }

    private static async Task<Guid> RunnerIdAsync(NpgsqlConnection conn, string tenant)
    {
        await using var cmd = new NpgsqlCommand("SELECT \"Id\" FROM sessions.runners WHERE \"TenantId\" = @t", conn);
        cmd.Parameters.AddWithValue("t", tenant);
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<long> CountAsSuperuserAsync(string cs, string table, string tenant)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        return await Db.CountAsync(conn, table, tenant);
    }
}
