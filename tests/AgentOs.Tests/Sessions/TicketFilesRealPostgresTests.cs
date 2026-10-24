// REAL-POSTGRES — ticket files (ADR-0015) on a real database: the migration creates sessions.ticket_files, the EF store
// keeps tenants apart through every call, the unique (tenant, scope, path) index makes a re-upload a replace, bytea
// round-trips the bytes, and the RLS policy (RowLevelSecurity.Tables) binds the new table: as the app role with tenant A
// set, a query with NO tenant predicate sees only A's files and writing a row for B is refused.
// It creates a throwaway database on the given server and drops it afterwards — zero residue on a shared stack.
//   Gate: RUN_AGENTOS_E2E_REAL=true and AGENTOS_PG_CONN (a connection string to any database on the server, as a user
//   that may CREATE DATABASE — the dev/lab stack's superuser).

using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AgentOs.Domain.Files;
using AgentOs.Modules.Sessions.Files;
using AgentOs.Modules.Sessions.Persistence;
using AgentOs.Modules.Sessions.Persistence.Repositories;
using AgentOs.SharedKernel.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Sessions;

public sealed class TicketFilesRealPostgresTests
{
    private static string? ServerConnection =>
        string.Equals(Environment.GetEnvironmentVariable("RUN_AGENTOS_E2E_REAL"), "true", StringComparison.OrdinalIgnoreCase)
            ? Environment.GetEnvironmentVariable("AGENTOS_PG_CONN")
            : null;

    [Fact]
    public async Task Store_OnRealPostgres_IsTenantIsolated_ReplacesByPath_AndRlsBindsTheTable()
    {
        var server = ServerConnection ?? string.Empty;
        if (server.Length == 0) { Assert.Skip("RUN_AGENTOS_E2E_REAL + AGENTOS_PG_CONN required."); }

        var database = "agentos_ticketfiles_" + Guid.NewGuid().ToString("N")[..12];
        var admin = new NpgsqlConnectionStringBuilder(server) { Database = "postgres", Pooling = false }.ConnectionString;
        var cs = new NpgsqlConnectionStringBuilder(server) { Database = database, Pooling = false }.ConnectionString;
        await Exec(admin, $"CREATE DATABASE \"{database}\"");
        try
        {
            var options = new DbContextOptionsBuilder<SessionsDbContext>()
                .UseNpgsql(cs, npg => npg.MigrationsHistoryTable("__EFMigrationsHistory", schema: "sessions")).Options;
            await using (var db = new SessionsDbContext(options))
            {
                await db.Database.MigrateAsync();
            }

            var scope = TicketFileScope.ForTicket(Guid.NewGuid(), 108);
            var files = new TicketFiles(new ContextScopes(options), TimeProvider.System);
            var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0, 255 };
            (await files.PutAsync("tenant-a", scope, "art/logo.png", png, "image/png", "Ann")).Ok.ShouldBeTrue();
            (await files.PutAsync("tenant-a", scope, "art/logo.png", [.. png, 1], "image/png", "Ann")).Ok.ShouldBeTrue();   // replace
            (await files.PutAsync("tenant-a", scope, "skills/SKILL.md", Encoding.UTF8.GetBytes("# Skill"), "text/markdown", "Ann")).Ok.ShouldBeTrue();
            (await files.PutAsync("tenant-b", scope, "art/logo.png", [9], "image/png", "Bob")).Ok.ShouldBeTrue();

            var a = await files.ListAsync("tenant-a", scope);
            a.Select(f => f.Path).ShouldBe(["art/logo.png", "skills/SKILL.md"]);
            a[0].Size.ShouldBe(7);
            (await files.GetAsync("tenant-a", scope, "art/logo.png"))!.Content.ShouldBe([.. png, 1]);
            (await files.GetAsync("tenant-b", scope, "art/logo.png"))!.Content.ShouldBe([9]);
            (await files.ForRunAsync("tenant-a", scope)).Select(f => f.Sha256).ShouldBe(a.Select(f => f.Sha256));
            (await files.DeleteAsync("tenant-b", scope, "skills/SKILL.md")).ShouldBeFalse();   // not B's file

            // Case-insensitive uniqueness at the database: the service refuses the twin in words, and the unique index on
            // (TenantId, ScopeKey, PathKey) refuses a raw twin row.
            (await files.PutAsync("tenant-a", scope, "ART/LOGO.PNG", [1], "image/png", "Ann")).Ok.ShouldBeFalse();
            var twin = await Should.ThrowAsync<PostgresException>(() => Exec(cs, $"""
                INSERT INTO sessions.ticket_files ("Id","TenantId","ScopeKey","Path","PathKey","Content","Size","Sha256","ContentType","UploadedBy","UploadedAtUtc")
                VALUES (gen_random_uuid(), 'tenant-a', '{scope}', 'ART/LOGO.PNG', 'art/logo.png', '\x00', 1, 'x', 'x', 'x', now())
                """));
            twin.SqlState.ShouldBe("23505");

            // RLS: bootstrap the policies on this database, then query as the app role without a tenant predicate.
            await using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync();
            await Exec(conn, RowLevelSecurity.BootstrapSql(conn.PostgreSqlVersion.Major));
            await Exec(conn, $"SET ROLE {RowLevelSecurity.Role}; SELECT set_config('{RowLevelSecurity.TenantSetting}', 'tenant-a', false)");
            (await Count(conn)).ShouldBe(2L);
            var refused = await Should.ThrowAsync<PostgresException>(() => Exec(conn, """
                INSERT INTO sessions.ticket_files ("Id","TenantId","ScopeKey","Path","PathKey","Content","Size","Sha256","ContentType","UploadedBy","UploadedAtUtc")
                VALUES (gen_random_uuid(), 'tenant-b', 'x', 'x', 'x', '\x00', 1, 'x', 'x', 'x', now())
                """));
            refused.SqlState.ShouldBe("42501");
            await Exec(conn, "RESET ROLE");

            (await files.DeleteScopeAsync("tenant-a", scope)).ShouldBe(2);
            (await files.ListAsync("tenant-b", scope)).ShouldHaveSingleItem();
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
        await Exec(conn, sql);
    }

    private static async Task Exec(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> Count(NpgsqlConnection conn)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM sessions.ticket_files";
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    // Each scope gets its own context on the real database (as the module registers it: scoped).
    private sealed class ContextScopes(DbContextOptions<SessionsDbContext> options)
        : Microsoft.Extensions.DependencyInjection.IServiceScopeFactory
    {
        public Microsoft.Extensions.DependencyInjection.IServiceScope CreateScope() => new Scope(new SessionsDbContext(options));

        private sealed class Scope(SessionsDbContext db) : Microsoft.Extensions.DependencyInjection.IServiceScope, IServiceProvider
        {
            private readonly EfScopedFileStore _store = new(db);
            public IServiceProvider ServiceProvider => this;
            public object? GetService(Type serviceType) => serviceType == typeof(IScopedFileStore) ? _store : null;
            public void Dispose() => db.Dispose();
        }
    }
}
