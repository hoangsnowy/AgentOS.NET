// RBAC v2 (E1.2) — per-board (workspace) membership storage + isolation. Both stores (EF, the stateless standalone's
// in-memory one) must agree: tenant-explicit, one row per (tenant, board, user), only workspace roles, never attach to
// another tenant's board, and a user's access map only ever reads their own tenant's rows.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentOs.Modules.Workspaces.Access;
using AgentOs.Modules.Workspaces.Persistence;
using AgentOs.Modules.Workspaces.Persistence.Entities;
using AgentOs.SharedKernel.Identity;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Workspaces;

public sealed class WorkspaceMembershipTests
{
    private static readonly Guid BoardA = Guid.NewGuid();
    private static readonly Guid BoardB = Guid.NewGuid();

    public static TheoryData<string> Stores() => ["ef", "memory"];

    private static async Task<IWorkspaceMembershipStore> StoreAsync(string kind)
    {
        if (kind == "memory") { return new InMemoryWorkspaceMembershipStore(TimeProvider.System); }
        var options = new DbContextOptionsBuilder<WorkspacesDbContext>().UseInMemoryDatabase($"ws-members-{Guid.NewGuid()}").Options;
        var db = new WorkspacesDbContext(options);
        db.Workspaces.AddRange(
            new WorkspaceEntity { Id = BoardA, TenantId = "acme", Name = "a", CredentialRef = "c" },
            new WorkspaceEntity { Id = BoardB, TenantId = "globex", Name = "b", CredentialRef = "c" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return new EfWorkspaceMembershipStore(db, TimeProvider.System);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Set_InsertsThenReplaces_OneRowPerUser(string kind)
    {
        var store = await StoreAsync(kind);
        await store.SetAsync("acme", BoardA, "u1", "viewer", "admin-1", TestContext.Current.CancellationToken);
        await store.SetAsync("acme", BoardA, "u1", "developer", "admin-1", TestContext.Current.CancellationToken);

        var rows = await store.ListAsync("acme", BoardA, TestContext.Current.CancellationToken);
        rows.ShouldHaveSingleItem().Role.ShouldBe("developer");
        rows[0].AddedByUserId.ShouldBe("admin-1");
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Set_RefusesRolesABoardCannotCarry(string kind)
    {
        var store = await StoreAsync(kind);
        foreach (var role in new[] { "admin", "auditor", "member", "platform-admin", "" })
        {
            await Should.ThrowAsync<ArgumentException>(() => store.SetAsync("acme", BoardA, "u1", role, null, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Set_EfStore_RefusesABoardOfAnotherTenant()
    {
        var store = await StoreAsync("ef");
        await Should.ThrowAsync<KeyNotFoundException>(() => store.SetAsync("acme", BoardB, "u1", "viewer", null, TestContext.Current.CancellationToken));
        (await store.ListForTenantAsync("globex", TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Reads_AreTenantScoped(string kind)
    {
        var store = await StoreAsync(kind);
        await store.SetAsync("acme", BoardA, "u1", "viewer", null, TestContext.Current.CancellationToken);

        (await store.ListForTenantAsync("acme", TestContext.Current.CancellationToken)).Count.ShouldBe(1);
        (await store.ListForTenantAsync("globex", TestContext.Current.CancellationToken)).ShouldBeEmpty();
        (await store.ListAsync("globex", BoardA, TestContext.Current.CancellationToken)).ShouldBeEmpty();
        (await store.RemoveAsync("globex", BoardA, "u1", TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await store.ListAsync("acme", BoardA, TestContext.Current.CancellationToken)).Count.ShouldBe(1);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Remove_DeletesOnlyThatMembership(string kind)
    {
        var store = await StoreAsync(kind);
        await store.SetAsync("acme", BoardA, "u1", "viewer", null, TestContext.Current.CancellationToken);
        await store.SetAsync("acme", BoardA, "u2", "approver", null, TestContext.Current.CancellationToken);

        (await store.RemoveAsync("acme", BoardA, "u1", TestContext.Current.CancellationToken)).ShouldBeTrue();
        (await store.RemoveAsync("acme", BoardA, "u1", TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await store.ListAsync("acme", BoardA, TestContext.Current.CancellationToken)).Select(m => m.UserId).ShouldBe(["u2"]);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Access_ComesFromTheCallersTenantOnly(string kind)
    {
        var store = await StoreAsync(kind);
        await store.SetAsync("acme", BoardA, "u1", "developer", null, TestContext.Current.CancellationToken);
        var access = new WorkspaceAccess(store);

        // In acme the board is restricted: u1 (member) may run agents there, u2 (tenant developer, no membership) can't see it.
        (await access.ForUserAsync("acme", ["viewer"], "u1", TestContext.Current.CancellationToken)).Can(BoardA, Permission.RunAgents).ShouldBeTrue();
        (await access.ForUserAsync("acme", ["developer"], "u2", TestContext.Current.CancellationToken)).CanSee(BoardA).ShouldBeFalse();
        // The same user id in another tenant inherits nothing from acme's memberships.
        var other = await access.ForUserAsync("globex", ["viewer"], "u1", TestContext.Current.CancellationToken);
        other.IsRestricted(BoardA).ShouldBeFalse();
        other.Can(BoardA, Permission.RunAgents).ShouldBeFalse();
        // A malformed sign-in (no tenant) reads nothing.
        (await access.ForUserAsync("", ["admin"], "u1", TestContext.Current.CancellationToken)).IsRestricted(BoardA).ShouldBeFalse();
    }
}
