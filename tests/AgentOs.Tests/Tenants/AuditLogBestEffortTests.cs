// IAuditLog's contract: a write is best-effort and NEVER throws to the caller — the surrounding operation (a sign-up, a
// member removal, a settings save) already happened. An entry that names no tenant used to throw out of the writer
// (the RLS tenant scope refuses a blank tenant); it is now refused like a database failure: nothing is stored, the loss
// is logged as an error and counted on agentos.audit.lost{reason=no_tenant}.

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading.Tasks;
using AgentOs.Modules.Tenants;
using AgentOs.Modules.Tenants.Persistence;
using AgentOs.Modules.Tenants.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Shouldly;
using Xunit;

namespace AgentOs.Tests.Tenants;

public sealed class AuditLogBestEffortTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task WriteAsync_EntryWithoutATenant_NeverThrows_StoresNothing_LogsAndCountsTheLoss(string? tenant)
    {
        var lost = new List<string?>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == AuditTelemetry.MeterName && instrument.Name == AuditTelemetry.Lost) { l.EnableMeasurementEvents(instrument); }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            string? reason = null;
            foreach (var tag in tags) { if (tag.Key == "reason") { reason = tag.Value as string; } }
            lock (lost) { for (var i = 0; i < value; i++) { lost.Add(reason); } }
        });
        listener.Start();

        var options = new DbContextOptionsBuilder<TenantsDbContext>().UseInMemoryDatabase($"audit-no-tenant-{Guid.NewGuid()}").Options;
        await using var db = new TenantsDbContext(options);
        var logger = new FakeLogger<EfAuditLog>();
        var log = new EfAuditLog(db, logger);
        var entry = new AuditEntry(Guid.NewGuid(), tenant!, "u", AuditActions.MemberRemoved, "bob", null, null, DateTimeOffset.UtcNow);

        await Should.NotThrowAsync(() => log.WriteAsync(entry, TestContext.Current.CancellationToken));

        (await db.AuditEvents.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
        lock (lost) { lost.ShouldContain(AuditTelemetry.Reasons.NoTenant); }
        var record = logger.Collector.GetSnapshot().ShouldHaveSingleItem();
        record.Level.ShouldBe(LogLevel.Error);
        record.Message.ShouldContain(entry.Id.ToString());
    }

    // The writer still works for a real tenant right after (nothing was left tracked or half-scoped).
    [Fact]
    public async Task WriteAsync_AfterARefusedEntry_TheNextEntryIsRecorded()
    {
        var options = new DbContextOptionsBuilder<TenantsDbContext>().UseInMemoryDatabase($"audit-after-refusal-{Guid.NewGuid()}").Options;
        await using var db = new TenantsDbContext(options);
        var log = new EfAuditLog(db, new FakeLogger<EfAuditLog>());

        await log.WriteAsync(new AuditEntry(Guid.NewGuid(), "", "u", AuditActions.MemberRemoved, null, null, null, DateTimeOffset.UtcNow),
            TestContext.Current.CancellationToken);
        await log.WriteAsync(new AuditEntry(Guid.NewGuid(), "acme", "u", AuditActions.MemberRemoved, null, null, null, DateTimeOffset.UtcNow),
            TestContext.Current.CancellationToken);

        (await log.ListAsync("acme", ct: TestContext.Current.CancellationToken)).ShouldHaveSingleItem().Sequence.ShouldBe(1);
        (await db.AuditEvents.Select(e => e.TenantId).ToListAsync(TestContext.Current.CancellationToken)).ShouldBe(["acme"]);
    }
}
