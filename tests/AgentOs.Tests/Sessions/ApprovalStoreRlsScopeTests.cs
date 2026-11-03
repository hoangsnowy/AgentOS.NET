// RLS stage 2 (ADR-0012): "no tenant ⇒ nothing", so EfApprovalStore must bind a flow that acts for no tenant (the
// Autopilot pass, a background gate check) to the tenant it names — otherwise every unbound read returns nothing and an
// approval policy silently never sees its approvals. Without a database: a connection interceptor records the tenant the
// RLS interceptor would bind (RowLevelSecurity.CurrentTenant at connection open) and stops the open there.
// The real-Postgres twin with RLS switched on is ApprovalStoreRlsEnabledRealDbTests.

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Governance;
using AgentOs.Modules.Sessions.Persistence;
using AgentOs.Modules.Sessions.Persistence.Repositories;
using AgentOs.SharedKernel.Identity;
using AgentOs.SharedKernel.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Sessions;

public sealed class ApprovalStoreRlsScopeTests
{
    private const string A = "approvals-tenant-a";
    private const string B = "approvals-tenant-b";

    public static TheoryData<string> Paths => ["get", "find", "request", "decide", "list"];

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task EveryPath_UnboundFlow_IsBoundToTheTenantItNames(string path)
    {
        var probe = new TenantProbe();
        await using var db = Context(probe);

        await Should.ThrowAsync<OpenStopped>(() => Call(new EfApprovalStore(db), path, A));

        probe.Seen.ShouldBe([A], $"{path}: the connection must open bound to the named tenant");
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task EveryPath_FlowBoundToAnotherTenant_IsNeverWidenedToTheNamedOne(string path)
    {
        var probe = new TenantProbe();
        await using var db = Context(probe);

        using (AmbientIdentity.Push(B, "u"))
        {
            await Should.ThrowAsync<OpenStopped>(() => Call(new EfApprovalStore(db), path, A));
        }

        probe.Seen.ShouldBe([B], $"{path}: a flow bound to B keeps B even when it names A");
    }

    [Fact]
    public async Task Scope_EndsWithTheCall()
    {
        var probe = new TenantProbe();
        await using var db = Context(probe);

        await Should.ThrowAsync<OpenStopped>(() => new EfApprovalStore(db).FindAsync(A, Guid.NewGuid()));

        RowLevelSecurity.CurrentTenant().ShouldBeNull();
    }

    private static Task Call(EfApprovalStore store, string path, string tenant) => path switch
    {
        "get" => store.GetAsync(tenant, Guid.NewGuid(), ApprovalGate.Merge, 0),
        "find" => store.FindAsync(tenant, Guid.NewGuid()),
        "request" => store.RequestAsync(ApprovalRequest.New(tenant, Guid.NewGuid(), Guid.NewGuid(), 7, ApprovalGate.Merge, 0,
            "#7 Add dark mode", null, "Autopilot", DateTimeOffset.UtcNow)),
        "decide" => store.DecideAsync(tenant, Guid.NewGuid(), new ApprovalDecision(true, "alice", null, DateTimeOffset.UtcNow)),
        _ => store.ListRecentAsync(tenant),
    };

    // Npgsql provider (so EF really opens a connection), an address that is never dialled: the probe stops the open.
    private static SessionsDbContext Context(TenantProbe probe) => new(new DbContextOptionsBuilder<SessionsDbContext>()
        .UseNpgsql("Host=approvals-probe.invalid;Database=none;Username=none;Password=none")
        .AddInterceptors(probe)
        .Options);

    private sealed class OpenStopped : Exception
    {
        public OpenStopped() : base("The probe stops every connection open.") { }
    }

    private sealed class TenantProbe : DbConnectionInterceptor
    {
        public List<string?> Seen { get; } = [];

        public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
        {
            Seen.Add(RowLevelSecurity.CurrentTenant());
            throw new OpenStopped();
        }

        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            Seen.Add(RowLevelSecurity.CurrentTenant());
            throw new OpenStopped();
        }
    }
}
