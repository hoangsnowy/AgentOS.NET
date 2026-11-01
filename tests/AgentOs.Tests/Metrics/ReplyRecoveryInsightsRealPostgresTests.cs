// REAL-POSTGRES — Insights counts the Board agent runs that ended without their JSON reply and were asked once for it:
// the follow-up call is stored with KcId "reply-recovery", and the Dapper fast path folds the count into its latency
// pass (COUNT(*) FILTER …) per tenant and time range. It creates a throwaway database on the given server and drops it
// afterwards — zero residue on a shared stack.
//   Gate: RUN_AGENTOS_E2E_REAL=true and AGENTOS_PG_CONN (a connection string to any database on the server, as a user
//   that may CREATE DATABASE — the dev/lab stack's superuser).

using System;
using System.Threading.Tasks;
using AgentOs.Domain.Llm;
using AgentOs.Modules.Pipeline.Persistence;
using AgentOs.Modules.Pipeline.Persistence.Repositories;
using AgentOs.SharedKernel.Identity;
using AgentOs.SharedKernel.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Metrics;

public sealed class ReplyRecoveryInsightsRealPostgresTests
{
    private static string? ServerConnection =>
        string.Equals(Environment.GetEnvironmentVariable("RUN_AGENTOS_E2E_REAL"), "true", StringComparison.OrdinalIgnoreCase)
            ? Environment.GetEnvironmentVariable("AGENTOS_PG_CONN")
            : null;

    [Fact]
    public async Task InsightsExtra_OnRealPostgres_CountsRepliesAskedAgain_PerTenantAndRange()
    {
        var server = ServerConnection ?? string.Empty;
        if (server.Length == 0) { Assert.Skip("RUN_AGENTOS_E2E_REAL + AGENTOS_PG_CONN required."); }

        var database = "agentos_recovery_" + Guid.NewGuid().ToString("N")[..12];
        var admin = new NpgsqlConnectionStringBuilder(server) { Database = "postgres", Pooling = false }.ConnectionString;
        var cs = new NpgsqlConnectionStringBuilder(server) { Database = database, Pooling = false }.ConnectionString;
        await Exec(admin, $"CREATE DATABASE \"{database}\"");
        try
        {
            var options = new DbContextOptionsBuilder<PipelineDbContext>()
                .UseNpgsql(cs, npg => npg.MigrationsHistoryTable("__EFMigrationsHistory", schema: "pipeline")).Options;
            var now = DateTimeOffset.UtcNow;
            LlmCallRecord Call(string? purpose) =>
                new("Coding", "computer-default", "RemoteAgent", 1000, 100, 0, 0, 0, 0m, TimeSpan.FromSeconds(3), true, null, now) { Purpose = purpose };

            await using (var db = new PipelineDbContext(options, new Tenant("tenant-a")))
            {
                await db.Database.MigrateAsync();
                var repo = new PipelineRunRepository(db, new Tenant("tenant-a"));
                await repo.SaveAgentRunAsync(new AgentRunRecord(Guid.NewGuid(), "Coding · #102", "Coding", true, now, now,
                    [Call(null), Call(LlmCallPurposes.ReplyRecovery)]), "tenant-a");
                await repo.SaveAgentRunAsync(new AgentRunRecord(Guid.NewGuid(), "Reviewer · #102", "Reviewer", true, now, now,
                    [Call(LlmCallPurposes.ReplyRecovery)]), "tenant-a");
                await repo.SaveAgentRunAsync(new AgentRunRecord(Guid.NewGuid(), "Coding · #1", "Coding", true, now, now,
                    [Call(LlmCallPurposes.ReplyRecovery)]), "tenant-b");
            }

            await using var read = new PipelineDbContext(options, new Tenant("x"));
            var dapper = new PipelineRunRepository(read, new Tenant("x"), new NpgsqlConnectionFactory(cs));

            var a = await dapper.GetInsightsExtraForTenantAsync("tenant-a");
            a.RepliesAskedAgain.ShouldBe(2);
            a.ByAgentLatency.ShouldNotBeEmpty();   // the same pass still answers the latency buckets
            (await dapper.GetInsightsExtraForTenantAsync("tenant-b")).RepliesAskedAgain.ShouldBe(1);
            (await dapper.GetInsightsExtraForTenantAsync("tenant-a", now.AddMinutes(5))).RepliesAskedAgain.ShouldBe(0);
            (await dapper.GetInsightsExtraForTenantAsync("tenant-c")).RepliesAskedAgain.ShouldBe(0);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await Exec(admin, $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)");
        }
    }

    private static async Task Exec(string cs, string sql)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private sealed class Tenant(string id) : ITenantContext
    {
        public string TenantId { get; } = id;
        public string? UserId => "u";
        public string? UserName => "u";
        public System.Collections.Generic.IReadOnlyList<string> Roles { get; } = [];
        public bool IsAuthenticated => true;
        public bool IsAdmin => false;
    }
}
