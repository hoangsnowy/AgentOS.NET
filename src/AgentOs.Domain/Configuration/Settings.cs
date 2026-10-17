// AgentOs.Domain/Configuration/Settings.cs
// UI-first configuration contracts (ADR-0011). Modules declare their settings as descriptors next to the code
// that reads them; the AppConfig module resolves values with operator-lock → tenant → platform → seed →
// default precedence and validates every write. BCL-only so every module can depend on it.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace AgentOs.Domain.Configuration;

/// <summary>Who may set a value.</summary>
public enum SettingScope
{
    /// <summary>Platform-wide only (platform admin); tenants inherit it.</summary>
    Platform,

    /// <summary>A platform value that each tenant may override.</summary>
    Tenant,
}

/// <summary>Value type — drives validation and the UI control.</summary>
public enum SettingKind
{
    /// <summary>Free text.</summary>
    Text,

    /// <summary>Encrypted at rest, never returned to the UI (only "set" + a masked suffix).</summary>
    Secret,

    /// <summary>Whole number (range-checked when Min/Max are set).</summary>
    WholeNumber,

    /// <summary>Decimal number (range-checked when Min/Max are set).</summary>
    Number,

    /// <summary><c>true</c>/<c>false</c>.</summary>
    Boolean,

    /// <summary>One of <see cref="SettingDescriptor.Choices"/>.</summary>
    Choice,

    /// <summary>Absolute https:// URL.</summary>
    Url,

    /// <summary>An LLM model id (free text; the UI suggests catalogued models).</summary>
    Model,
}

/// <summary>Where an effective value came from.</summary>
public enum SettingSource
{
    /// <summary>The descriptor's built-in default.</summary>
    Default,

    /// <summary>Bootstrap configuration (appsettings / env) under the same key path.</summary>
    Seed,

    /// <summary>Set by a platform admin.</summary>
    Platform,

    /// <summary>Set by the tenant's admin.</summary>
    Tenant,

    /// <summary>Pinned by the operator (<c>Settings:Locks:&lt;key&gt;</c>); not editable from the UI.</summary>
    Locked,
}

/// <summary>A declared, typed, validated setting (ADR-0011).</summary>
/// <param name="Key">Colon path, identical to the appsettings path (e.g. <c>Agents:Coding:Model</c>).</param>
/// <param name="Kind">Value type.</param>
/// <param name="Category">UI grouping (e.g. <c>LLM providers</c>, <c>Agent profiles</c>).</param>
/// <param name="Label">Short UI label.</param>
public sealed record SettingDescriptor(string Key, SettingKind Kind, string Category, string Label)
{
    /// <summary>Built-in default (invariant-culture string). Null = unset.</summary>
    public string? DefaultValue { get; init; }

    /// <summary>Scope ceiling. Default: tenants may override.</summary>
    public SettingScope Scope { get; init; } = SettingScope.Tenant;

    /// <summary>One-line UI help.</summary>
    public string? Help { get; init; }

    /// <summary>Allowed values for <see cref="SettingKind.Choice"/>.</summary>
    public IReadOnlyList<string>? Choices { get; init; }

    /// <summary>Inclusive lower bound for numeric kinds.</summary>
    public decimal? Min { get; init; }

    /// <summary>Inclusive upper bound for numeric kinds.</summary>
    public decimal? Max { get; init; }

    /// <summary>Extra check after the kind check. Returns an error message, or null when valid.</summary>
    public Func<string, string?>? Validator { get; init; }

    /// <summary>True for <see cref="SettingKind.Secret"/>.</summary>
    public bool IsSecret => Kind == SettingKind.Secret;
}

/// <summary>An effective value plus its provenance.</summary>
public readonly record struct SettingValue(string? Value, SettingSource Source)
{
    /// <summary>True when a non-empty value resolved.</summary>
    public bool HasValue => !string.IsNullOrWhiteSpace(Value);
}

/// <summary>A setting as the UI sees it: descriptor, effective value (masked when secret), and source.</summary>
public sealed record SettingView(SettingDescriptor Descriptor, string? DisplayValue, SettingSource Source, bool IsSet);

/// <summary>The outcome of a write.</summary>
public sealed record SettingWriteResult(bool Ok, string? Error)
{
    /// <summary>Success.</summary>
    public static SettingWriteResult Success { get; } = new(true, null);

    /// <summary>Failure with a user-facing reason.</summary>
    public static SettingWriteResult Fail(string error) => new(false, error);
}

/// <summary>An audited change (secrets already redacted).</summary>
public sealed record SettingChange(
    string Key, SettingScope Scope, string? TenantId, string Actor, string? OldValue, string? NewValue, DateTimeOffset At);

/// <summary>A module's settings declarations. Registered in DI; collected once at startup.</summary>
public interface ISettingsContributor
{
    /// <summary>The descriptors this module owns.</summary>
    IEnumerable<SettingDescriptor> Describe();
}

/// <summary>All declared settings, keyed by <see cref="SettingDescriptor.Key"/> (case-insensitive).</summary>
public interface ISettingsCatalog
{
    /// <summary>Every descriptor, in declaration order.</summary>
    IReadOnlyList<SettingDescriptor> All { get; }

    /// <summary>O(1) lookup.</summary>
    bool TryGet(string key, out SettingDescriptor descriptor);
}

/// <summary>Records setting changes (implemented by the Tenants audit log; a no-op by default).</summary>
public interface ISettingsAuditSink
{
    /// <summary>Record a change. Must not throw.</summary>
    ValueTask RecordAsync(SettingChange change, CancellationToken cancellationToken = default);
}

/// <summary>Reads and writes declared settings with lock → tenant → platform → seed → default precedence.</summary>
public interface ISettings
{
    /// <summary>Monotonic counter bumped on every local write — cache derived objects against it.</summary>
    long Version { get; }

    /// <summary>The effective value for <paramref name="key"/>; <paramref name="tenantId"/> null = platform view.</summary>
    ValueTask<SettingValue> GetAsync(string key, string? tenantId, CancellationToken cancellationToken = default);

    /// <summary>Every descriptor in <paramref name="category"/> (or all when null) with its effective value; secrets masked.</summary>
    ValueTask<IReadOnlyList<SettingView>> ListAsync(string? tenantId, string? category = null, CancellationToken cancellationToken = default);

    /// <summary>Validate and store <paramref name="value"/> at <paramref name="scope"/>; empty clears the override.</summary>
    ValueTask<SettingWriteResult> SetAsync(
        string key, string? value, SettingScope scope, string? tenantId, string actor, CancellationToken cancellationToken = default);
}

/// <summary>Typed reads over <see cref="ISettings"/> (invariant culture; unparsable → fallback).</summary>
public static class SettingsExtensions
{
    /// <summary>String value, or <paramref name="fallback"/> when unset.</summary>
    public static async ValueTask<string?> GetStringAsync(
        this ISettings settings, string key, string? tenantId, string? fallback = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var v = await settings.GetAsync(key, tenantId, ct).ConfigureAwait(false);
        return v.HasValue ? v.Value : fallback;
    }

    /// <summary>Integer value, or <paramref name="fallback"/>.</summary>
    public static async ValueTask<int> GetIntAsync(
        this ISettings settings, string key, string? tenantId, int fallback, CancellationToken ct = default)
    {
        var s = await settings.GetStringAsync(key, tenantId, null, ct).ConfigureAwait(false);
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;
    }

    /// <summary>Decimal value, or <paramref name="fallback"/>.</summary>
    public static async ValueTask<decimal> GetDecimalAsync(
        this ISettings settings, string key, string? tenantId, decimal fallback, CancellationToken ct = default)
    {
        var s = await settings.GetStringAsync(key, tenantId, null, ct).ConfigureAwait(false);
        return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : fallback;
    }

    /// <summary>Boolean value, or <paramref name="fallback"/>.</summary>
    public static async ValueTask<bool> GetBoolAsync(
        this ISettings settings, string key, string? tenantId, bool fallback, CancellationToken ct = default)
    {
        var s = await settings.GetStringAsync(key, tenantId, null, ct).ConfigureAwait(false);
        return bool.TryParse(s, out var b) ? b : fallback;
    }
}
