// Shared BenchmarkDotNet configuration: allocations on every benchmark (owner directive 2 — avoidable allocations
// on a hot path are the most common waste), plus the brief JSON report the baseline comparer reads.

using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Order;

namespace AgentOs.Benchmarks;

/// <summary>The suite-wide config passed to <c>BenchmarkSwitcher</c> (CLI args such as <c>--job short</c> still apply).</summary>
public sealed class BenchmarkConfig : ManualConfig
{
    /// <summary>Default config + memory diagnoser + brief JSON (the format of <c>baseline/*.json</c>).</summary>
    public BenchmarkConfig()
    {
        Add(DefaultConfig.Instance);
        AddDiagnoser(MemoryDiagnoser.Default);
        AddExporter(JsonExporter.Brief);
        WithOrderer(new DefaultOrderer(SummaryOrderPolicy.Declared));
        WithOptions(ConfigOptions.DisableLogFile);
    }
}
