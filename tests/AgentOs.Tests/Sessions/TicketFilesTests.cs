// Ticket files (ADR-0015) — the path rules, the limits (UI settings), SHA-256, scope copy, and tenant isolation of the
// store (the Development in-memory store and the EF store on EF InMemory). The real-Postgres twin is
// TicketFilesRealPostgresTests.

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AgentOs.Domain.Configuration;
using AgentOs.Domain.Files;
using AgentOs.Modules.AppConfig;
using AgentOs.Modules.AppConfig.Settings;
using AgentOs.Modules.Sessions.Endpoints;
using AgentOs.Modules.Sessions.Files;
using AgentOs.Modules.Sessions.Persistence;
using AgentOs.Modules.Sessions.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Sessions;

public sealed class TicketFilesTests
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";
    private static readonly string Ticket = TicketFileScope.ForTicket(Guid.Parse("11111111-2222-3333-4444-555555555555"), 108);

    // ── Path rules ──────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("logo.png", "logo.png")]
    [InlineData("art\\icons\\logo.png", "art/icons/logo.png")]
    [InlineData("  skills//design/SKILL.md  ", "skills/design/SKILL.md")]
    [InlineData("docs/Đăng ký.md", "docs/Đăng ký.md")]
    [InlineData(".claude/skills/a.md", ".claude/skills/a.md")]
    public void TryNormalize_ValidPath_IsNormalised(string raw, string expected)
    {
        TicketFilePath.TryNormalize(raw, out var path, out var error).ShouldBeTrue(error);
        path.ShouldBe(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../secrets.txt")]
    [InlineData("art/../../x.png")]
    [InlineData("./logo.png")]
    [InlineData("/etc/passwd")]
    [InlineData("\\\\server\\share\\x")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("C:logo.png")]
    [InlineData("~/.ssh/id_rsa")]
    [InlineData("logo.png:hidden")]
    [InlineData("con.txt")]
    [InlineData("art/NUL")]
    [InlineData("trailing.")]
    [InlineData("a/ b")]
    [InlineData("bad\u0001name")]
    [InlineData("what?.png")]
    public void TryNormalize_UnsafePath_IsRefusedWithAReason(string raw)
    {
        TicketFilePath.TryNormalize(raw, out var path, out var error).ShouldBeFalse();
        path.ShouldBeEmpty();
        error.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void TryNormalize_TooLong_IsRefused()
    {
        var ok = string.Join('/', Enumerable.Repeat(new string('a', 99), 2));   // 199 characters
        TicketFilePath.TryNormalize(ok, out _, out _).ShouldBeTrue();
        TicketFilePath.TryNormalize(ok + "/ab", out _, out var error).ShouldBeFalse();
        error!.ShouldContain("too long");
        TicketFilePath.TryNormalize(new string('b', TicketFilePath.MaxSegmentLength + 1), out _, out _).ShouldBeFalse();
    }

    // ── Settings ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Limits_DefaultsOrTheTenantsValues_ClampedToTheirRanges()
    {
        var settings = Settings();
        (await TicketFileSettings.ReadAsync(settings, TenantA)).ShouldBe(TicketFileLimits.Default);
        TicketFileLimits.Default.ShouldBe(new TicketFileLimits(10L << 20, 50L << 20, 500));

        (await settings.SetAsync(TicketFileSettings.MaxFileMegabytesKey, "2", SettingScope.Tenant, TenantA, "admin")).Ok.ShouldBeTrue();
        (await settings.SetAsync(TicketFileSettings.MaxFilesKey, "5000", SettingScope.Tenant, TenantA, "admin")).Ok.ShouldBeFalse();   // out of range
        var limits = await TicketFileSettings.ReadAsync(settings, TenantA);
        limits.MaxFileBytes.ShouldBe(2L << 20);
        (await TicketFileSettings.ReadAsync(settings, TenantB)).ShouldBe(TicketFileLimits.Default);   // per tenant
        new TicketFileSettings().Describe().Select(d => d.Key).ShouldBe(TicketFileSettings.Keys);
        new TicketFileSettings().Describe().ShouldAllBe(d => d.Category == "Autopilot" && d.Kind == SettingKind.WholeNumber);
    }

    // ── The service over the store ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Put_StoresNormalisedPath_Sha256_AndReplacesTheSamePath()
    {
        var (files, _) = Service();
        var first = await files.PutAsync(TenantA, Ticket, "art\\logo.png", Bytes("v1"), "image/png", "Ann");
        first.Ok.ShouldBeTrue(first.Error);
        first.File!.Path.ShouldBe("art/logo.png");
        first.File.Sha256.ShouldBe(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Bytes("v1"))));
        first.File.Size.ShouldBe(2);
        first.File.UploadedBy.ShouldBe("Ann");

        (await files.PutAsync(TenantA, Ticket, "art/logo.png", Bytes("v2-longer"), "image/png", "Bob")).Ok.ShouldBeTrue();

        var list = await files.ListAsync(TenantA, Ticket);
        list.ShouldHaveSingleItem().Size.ShouldBe(9);
        list[0].UploadedBy.ShouldBe("Bob");
        Encoding.UTF8.GetString((await files.GetAsync(TenantA, Ticket, "art/logo.png"))!.Content).ShouldBe("v2-longer");
    }

    [Fact]
    public async Task Put_BadPath_IsRefusedAndNothingStored()
    {
        var (files, _) = Service();
        var put = await files.PutAsync(TenantA, Ticket, "../../etc/passwd", Bytes("x"), null, "Ann");
        put.Ok.ShouldBeFalse();
        put.Error!.ShouldContain("..");
        (await files.ListAsync(TenantA, Ticket)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Put_OverTheLimits_IsRefusedInPlainWords()
    {
        var settings = Settings();
        (await settings.SetAsync(TicketFileSettings.MaxFileMegabytesKey, "1", SettingScope.Tenant, TenantA, "admin")).Ok.ShouldBeTrue();
        (await settings.SetAsync(TicketFileSettings.MaxTotalMegabytesKey, "1", SettingScope.Tenant, TenantA, "admin")).Ok.ShouldBeTrue();
        (await settings.SetAsync(TicketFileSettings.MaxFilesKey, "2", SettingScope.Tenant, TenantA, "admin")).Ok.ShouldBeTrue();
        var (files, _) = Service(settings);

        var tooBig = await files.PutAsync(TenantA, Ticket, "big.bin", new byte[(1 << 20) + 1], null, "Ann");
        tooBig.Ok.ShouldBeFalse();
        tooBig.Error!.ShouldContain("at most 1 MB");

        (await files.PutAsync(TenantA, Ticket, "a.bin", new byte[600 * 1024], null, "Ann")).Ok.ShouldBeTrue();
        var overTotal = await files.PutAsync(TenantA, Ticket, "b.bin", new byte[600 * 1024], null, "Ann");
        overTotal.Ok.ShouldBeFalse();
        overTotal.Error!.ShouldContain("together");

        (await files.PutAsync(TenantA, Ticket, "b.txt", Bytes("b"), null, "Ann")).Ok.ShouldBeTrue();
        var third = await files.PutAsync(TenantA, Ticket, "c.txt", Bytes("c"), null, "Ann");
        third.Ok.ShouldBeFalse();
        third.Error!.ShouldContain("the most it can have is 2");
        // Replacing an existing path is not one more file.
        (await files.PutAsync(TenantA, Ticket, "b.txt", Bytes("bb"), null, "Ann")).Ok.ShouldBeTrue();
    }

    [Fact]
    public async Task Put_SameNameOtherCapitals_IsRefused_WindowsWouldMergeThem()
    {
        var (files, _) = Service();
        (await files.PutAsync(TenantA, Ticket, "Logo.png", Bytes("a"), null, "Ann")).Ok.ShouldBeTrue();
        var clash = await files.PutAsync(TenantA, Ticket, "logo.PNG", Bytes("b"), null, "Ann");
        clash.Ok.ShouldBeFalse();
        clash.Error!.ShouldContain("capitals");
    }

    // The stores key a file by TicketFilePath.Key (invariant lower case), like the database's unique index: a second
    // put with other capitals replaces the one file (and takes its spelling), never adds a twin.
    [Fact]
    public async Task Stores_LogoThenLOGO_AreOneFile_InMemoryAndEf()
    {
        var info = new StoredFileInfo(Guid.Empty, TenantA, Ticket, "Logo.png", 1, RunFile.Hash(Bytes("a")), "image/png", "Ann", DateTimeOffset.UtcNow);
        var memory = new InMemoryScopedFileStore();
        var options = new DbContextOptionsBuilder<SessionsDbContext>().UseInMemoryDatabase($"ticket-files-case-{Guid.NewGuid()}").Options;
        await using var db = new SessionsDbContext(options);
        foreach (var store in new IScopedFileStore[] { memory, new EfScopedFileStore(db) })
        {
            await store.PutAsync(info, Bytes("a"));
            await store.PutAsync(info with { Path = "LOGO.PNG" }, Bytes("bb"));

            var file = (await store.ListAsync(TenantA, Ticket)).ShouldHaveSingleItem();
            file.Path.ShouldBe("LOGO.PNG");
            file.Size.ShouldBe(2);
            (await store.GetAsync(TenantA, Ticket, "logo.png"))!.Content.ShouldBe(Bytes("bb"));
            (await store.DeleteAsync(TenantA, Ticket, "Logo.PNG")).ShouldBeTrue();
            (await store.ListAsync(TenantA, Ticket)).ShouldBeEmpty();
        }
        TicketFilePath.Key("Art/Logo.PNG").ShouldBe("art/logo.png");
    }

    [Fact]
    public async Task Files_AreStrictlyPerTenant()
    {
        var (files, _) = Service();
        (await files.PutAsync(TenantA, Ticket, "logo.png", Bytes("a"), null, "Ann")).Ok.ShouldBeTrue();

        (await files.ListAsync(TenantB, Ticket)).ShouldBeEmpty();
        (await files.GetAsync(TenantB, Ticket, "logo.png")).ShouldBeNull();
        (await files.ForRunAsync(TenantB, Ticket)).ShouldBeEmpty();
        (await files.DeleteAsync(TenantB, Ticket, "logo.png")).ShouldBeFalse();
        (await files.DeleteScopeAsync(TenantB, Ticket)).ShouldBe(0);
        (await files.ListAsync(TenantA, Ticket)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task CopyScope_DraftFilesGoToTheTicket_ForRunCarriesThem()
    {
        var (files, _) = Service();
        var draft = TicketFileScope.ForDraft(Guid.NewGuid());
        (await files.PutAsync(TenantA, draft, "skills/design/SKILL.md", Bytes("# Skill"), "text/markdown", "Ann")).Ok.ShouldBeTrue();
        (await files.PutAsync(TenantA, draft, "logo.png", Bytes("png"), "image/png", "Ann")).Ok.ShouldBeTrue();

        (await files.CopyScopeAsync(TenantA, draft, Ticket)).ShouldBe(2);
        (await files.DeleteScopeAsync(TenantA, draft)).ShouldBe(2);

        var run = await files.ForRunAsync(TenantA, Ticket);
        run.Select(f => f.Path).ShouldBe(["logo.png", "skills/design/SKILL.md"]);
        run[1].Sha256.ShouldBe(RunFile.Hash(Bytes("# Skill")));
        run[1].ContentType.ShouldBe("text/markdown");
    }

    [Fact]
    public async Task Delete_RemovesOnlyThatFile()
    {
        var (files, _) = Service();
        await files.PutAsync(TenantA, Ticket, "a.txt", Bytes("a"), null, "Ann");
        await files.PutAsync(TenantA, Ticket, "b.txt", Bytes("b"), null, "Ann");

        (await files.DeleteAsync(TenantA, Ticket, "a.txt")).ShouldBeTrue();
        (await files.DeleteAsync(TenantA, Ticket, "a.txt")).ShouldBeFalse();
        (await files.ListAsync(TenantA, Ticket)).ShouldHaveSingleItem().Path.ShouldBe("b.txt");
    }

    [Fact]
    public void Scope_TaskWithoutANumber_UsesItsOwnScope()
    {
        var board = Guid.NewGuid();
        var session = Guid.NewGuid();
        TicketFileScope.ForTask(board, 7, session).ShouldBe(TicketFileScope.ForTicket(board, 7));
        TicketFileScope.ForTask(board, null, session).ShouldBe(TicketFileScope.ForSession(session));
        TicketFileScope.ForTask(board, 0, session).ShouldBe(TicketFileScope.ForSession(session));
        TicketFileScope.ForTicket(board, int.MaxValue).Length.ShouldBeLessThanOrEqualTo(TicketFileScope.MaxLength);
    }

    // ── The EF store (EF InMemory): same contract, tenant-explicit ──────────────────────────────

    [Fact]
    public async Task EfStore_PutListReplaceDelete_TenantExplicit()
    {
        var options = new DbContextOptionsBuilder<SessionsDbContext>().UseInMemoryDatabase($"ticket-files-{Guid.NewGuid()}").Options;
        var info = new StoredFileInfo(Guid.Empty, TenantA, Ticket, "logo.png", 1, RunFile.Hash(Bytes("a")), "image/png", "Ann", DateTimeOffset.UtcNow);
        await using (var db = new SessionsDbContext(options))
        {
            var store = new EfScopedFileStore(db);
            await store.PutAsync(info, Bytes("a"));
            await store.PutAsync(info with { Path = "b.txt" }, Bytes("bb"));
            await store.PutAsync(info with { TenantId = TenantB }, Bytes("other tenant"));
            await store.PutAsync(info, Bytes("a2"));   // replace
        }
        await using (var db = new SessionsDbContext(options))
        {
            var store = new EfScopedFileStore(db);
            var listed = await store.ListAsync(TenantA, Ticket);
            listed.Select(f => f.Path).ShouldBe(["b.txt", "logo.png"]);
            listed.Single(f => f.Path == "logo.png").Size.ShouldBe(2);
            (await store.GetAsync(TenantB, Ticket, "logo.png"))!.Content.ShouldBe(Bytes("other tenant"));
            (await store.LoadAllAsync(TenantA, Ticket)).Count.ShouldBe(2);
            (await store.DeleteAsync(TenantA, Ticket, "logo.png")).ShouldBeTrue();
            (await store.DeleteScopeAsync(TenantA, Ticket)).ShouldBe(1);
            (await store.ListAsync(TenantA, Ticket)).ShouldBeEmpty();
            (await store.ListAsync(TenantB, Ticket)).ShouldHaveSingleItem();   // the other tenant's file is untouched
        }
    }

    // ── REST upload reads a bounded body ────────────────────────────────────────────────────────

    [Fact]
    public async Task ReadBounded_StopsPastTheLimit()
    {
        using var small = new MemoryStream(new byte[100]);
        (await TicketFileEndpoints.ReadBoundedAsync(small, 100, default))!.Length.ShouldBe(100);
        using var big = new MemoryStream(new byte[101]);
        (await TicketFileEndpoints.ReadBoundedAsync(big, 100, default)).ShouldBeNull();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    private static SettingsService Settings() => new(
        new SettingsCatalog([new TicketFileSettings()]), new InMemoryAppConfigStore(),
        new ConfigurationBuilder().Build(), new NullSettingsAuditSink(), NullLogger<SettingsService>.Instance);

    private static (ITicketFiles Files, IScopedFileStore Store) Service(ISettings? settings = null)
    {
        var store = new InMemoryScopedFileStore();
        return (new TicketFiles(new StoreScopes(store), TimeProvider.System, settings ?? Settings()), store);
    }

    // A scope factory whose every scope resolves the one store (nothing to dispose).
    internal sealed class StoreScopes(IScopedFileStore store) : IServiceScopeFactory, IServiceScope, IServiceProvider
    {
        public IServiceScope CreateScope() => this;
        public IServiceProvider ServiceProvider => this;
        public object? GetService(Type serviceType) => serviceType == typeof(IScopedFileStore) ? store : null;
        public void Dispose() { }
    }
}
