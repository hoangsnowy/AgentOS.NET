// The one "AgentOs.Tools" Meter (exported by ServiceDefaults' OTel metrics). It lives in Domain so the gateway
// (Domain) and the evidence buffer (Modules.Tools) publish on the same instance.

using System.Diagnostics.Metrics;

namespace AgentOs.Domain.Tools;

/// <summary>Tool-side instruments: evidence health + tool-output shaping savings.</summary>
public static class ToolTelemetry
{
    /// <summary>Meter name (registered with <c>.AddMeter("AgentOs.Tools")</c>).</summary>
    public const string MeterName = "AgentOs.Tools";

    /// <summary>Name of the shaping-savings counter.</summary>
    public const string SavedCharsName = "agentos.tools.output.saved_chars";

    /// <summary>The shared meter.</summary>
    public static Meter Meter { get; } = new(MeterName);

    /// <summary>Characters of tool output kept away from the model by shaping (tag <c>tool</c>).</summary>
    public static Counter<long> SavedChars { get; } = Meter.CreateCounter<long>(
        SavedCharsName, unit: "{char}",
        description: "Tool-output characters the gateway kept away from the model (log shaping + size cap); ≈ chars/4 tokens.");
}
