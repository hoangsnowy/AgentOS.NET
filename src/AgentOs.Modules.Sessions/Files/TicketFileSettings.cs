// Ticket files (ADR-0015) — the three limits, as UI settings (ADR-0011), per tenant: Settings › AI › Autopilot ›
// Files for the AI. Read on every upload, so a change applies to the next file without a restart.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Domain.Configuration;
using AgentOs.Domain.Files;

namespace AgentOs.Modules.Sessions.Files;

/// <summary>Declares the <c>TicketFiles:*</c> settings.</summary>
public sealed class TicketFileSettings : ISettingsContributor
{
    /// <summary>Shown with Autopilot's settings (the page that governs what agents get).</summary>
    public const string Category = "Autopilot";

    /// <summary>The largest single file (MB).</summary>
    public const string MaxFileMegabytesKey = "TicketFiles:MaxFileMegabytes";

    /// <summary>All files of one ticket together (MB).</summary>
    public const string MaxTotalMegabytesKey = "TicketFiles:MaxTotalMegabytes";

    /// <summary>Files per ticket.</summary>
    public const string MaxFilesKey = "TicketFiles:MaxFiles";

    private const long Megabyte = 1024L * 1024;

    private static readonly (string Key, int Min, int Max, int Default)[] Bounds =
    [
        (MaxFileMegabytesKey, 1, 100, 10),
        (MaxTotalMegabytesKey, 1, 500, 50),
        (MaxFilesKey, 1, 2000, 500),
    ];

    /// <summary>Every limit key, in page order.</summary>
    public static IReadOnlyList<string> Keys { get; } = [MaxFileMegabytesKey, MaxTotalMegabytesKey, MaxFilesKey];

    /// <inheritdoc />
    public IEnumerable<SettingDescriptor> Describe()
    {
        yield return Bound(MaxFileMegabytesKey, "Largest file (MB)",
            "The biggest single file a person can attach to a ticket for the AI (a logo, a picture, a document).");
        yield return Bound(MaxTotalMegabytesKey, "All files of a ticket (MB)",
            "How much one ticket's files may add up to. They are copied to the computer for every run of the ticket.");
        yield return Bound(MaxFilesKey, "Files per ticket",
            "How many files one ticket may have — an uploaded folder counts each file in it.");
    }

    /// <summary>The tenant's limits, read now and clamped to their ranges.</summary>
    public static async ValueTask<TicketFileLimits> ReadAsync(ISettings settings, string tenantId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var v = new int[Bounds.Length];
        for (var i = 0; i < Bounds.Length; i++)
        {
            var (key, min, max, fallback) = Bounds[i];
            v[i] = Math.Clamp(await settings.GetIntAsync(key, tenantId, fallback, ct).ConfigureAwait(false), min, max);
        }
        return new TicketFileLimits(v[0] * Megabyte, v[1] * Megabyte, v[2]);
    }

    private static SettingDescriptor Bound(string key, string label, string help)
    {
        var b = Array.Find(Bounds, x => x.Key == key);
        return new(key, SettingKind.WholeNumber, Category, label)
        {
            DefaultValue = b.Default.ToString(CultureInfo.InvariantCulture),
            Min = b.Min,
            Max = b.Max,
            Help = help,
        };
    }
}
