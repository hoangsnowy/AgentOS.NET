// Kind-based validation for setting values (ADR-0011). Allocation-free parsing (invariant culture), no regex.

using System;
using System.Globalization;
using AgentOs.Domain.Configuration;

namespace AgentOs.Modules.AppConfig.Settings;

/// <summary>Validates a non-empty value against its descriptor.</summary>
public static class SettingValidation
{
    /// <summary>Upper bound on any stored value — settings are configuration, not documents.</summary>
    public const int MaxLength = 8_192;

    /// <summary>Returns a user-facing error, or null when <paramref name="value"/> is valid for <paramref name="d"/>.</summary>
    public static string? Validate(SettingDescriptor d, string value)
    {
        ArgumentNullException.ThrowIfNull(d);
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length > MaxLength)
        {
            return $"{d.Label} is too long (max {MaxLength} characters).";
        }

        var kindError = d.Kind switch
        {
            SettingKind.WholeNumber => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                ? Range(d, n)
                : $"{d.Label} must be a whole number.",
            SettingKind.Number => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var m)
                ? Range(d, m)
                : $"{d.Label} must be a number.",
            SettingKind.Boolean => bool.TryParse(value, out _) ? null : $"{d.Label} must be true or false.",
            SettingKind.Choice => IsChoice(d, value) ? null : $"{d.Label} must be one of: {string.Join(", ", d.Choices ?? [])}.",
            SettingKind.Url => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
                ? null
                : $"{d.Label} must be an absolute https:// URL.",
            SettingKind.Model => IsModelId(value) ? null : $"{d.Label} must be a model id (letters, digits, . - _ : @ /).",
            _ => null,
        };

        return kindError ?? d.Validator?.Invoke(value);
    }

    private static string? Range(SettingDescriptor d, decimal v) =>
        (d.Min is { } min && v < min) || (d.Max is { } max && v > max)
            ? $"{d.Label} must be between {d.Min?.ToString(CultureInfo.InvariantCulture) ?? "-∞"} and {d.Max?.ToString(CultureInfo.InvariantCulture) ?? "∞"}."
            : null;

    private static bool IsChoice(SettingDescriptor d, string value)
    {
        if (d.Choices is null)
        {
            return true;
        }
        foreach (var c in d.Choices)
        {
            if (string.Equals(c, value, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    // Model ids / deployment names: claude-sonnet-5-5, gpt-4.1, gpt-4o-2024-05-13, claude-opus-4-5@20251101, org/model.
    private static bool IsModelId(string value)
    {
        if (value.Length is 0 or > 200)
        {
            return false;
        }
        foreach (var c in value)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or ':' or '@' or '/'))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Mask a secret for display: never the value, only a short suffix when it is long enough to be safe.</summary>
    public static string Mask(string secret) =>
        secret.Length >= 12 ? string.Concat("••••", secret.AsSpan(secret.Length - 4)) : "••••";
}
