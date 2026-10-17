// Default audit sink when no module provides one (standalone, or before the Tenants audit log is wired).

using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Configuration;

namespace AgentOs.Modules.AppConfig.Settings;

/// <summary>Discards setting-change records.</summary>
public sealed class NullSettingsAuditSink : ISettingsAuditSink
{
    /// <inheritdoc />
    public ValueTask RecordAsync(SettingChange change, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
