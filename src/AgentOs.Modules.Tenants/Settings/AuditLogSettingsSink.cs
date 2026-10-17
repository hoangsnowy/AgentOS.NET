// ISettingsAuditSink → the tenant audit trail (plan C.3). Every settings write (Settings UI, Agent profiles,
// Platform) lands in tenants.audit_events as `setting.changed` with "key: old → new" — values arrive already
// redacted for secrets (SettingsService masks them) and are clipped to the column. Platform-scope changes are
// filed under AuditScopes.Platform, a tenant id no tenant can have (slugs never contain '_'), so only platform
// admins see them. Best-effort like every audit write: it never throws into the settings write.

using System;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentOs.Modules.Tenants.Settings;

/// <summary>Records setting changes in the tenant audit log. Singleton; resolves the scoped log per write.</summary>
internal sealed partial class AuditLogSettingsSink(IServiceScopeFactory scopes, ILogger<AuditLogSettingsSink> logger) : ISettingsAuditSink
{
    private const int TargetMax = 256;   // tenants.audit_events.Target

    public async ValueTask RecordAsync(SettingChange change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        try
        {
            var scope = scopes.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                var log = scope.ServiceProvider.GetRequiredService<IAuditLog>();
                await log.WriteAsync(new AuditEntry(
                    Guid.NewGuid(),
                    change.Scope == SettingScope.Platform || string.IsNullOrWhiteSpace(change.TenantId) ? AuditScopes.Platform : change.TenantId,
                    change.Actor,
                    AuditActions.SettingChanged,
                    Target(change),
                    IpAddress: null,
                    UserAgent: null,
                    change.At), cancellationToken).ConfigureAwait(false);
            }
        }
#pragma warning disable CA1031 // The audit write is best-effort and must never fail the settings write.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogFailed(logger, ex);
        }
    }

    /// <summary>"Agents:Coding:Model: claude-sonnet-4-6 → claude-opus-5" (unset shown as ∅), clipped to the column.</summary>
    internal static string Target(SettingChange change)
    {
        var text = $"{change.Key}: {Value(change.OldValue)} → {Value(change.NewValue)}";
        return text.Length <= TargetMax ? text : string.Concat(text.AsSpan(0, TargetMax - 1), "…");

        static string Value(string? v) => string.IsNullOrEmpty(v) ? "∅" : v.ReplaceLineEndings(" ");
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Settings audit write failed — the setting itself was saved.")]
    private static partial void LogFailed(ILogger logger, Exception ex);
}
