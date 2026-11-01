// ToolOutputShaper runs on every tool result headed back to the model (AIToolFunction / the LLM tool loop). The worst
// realistic input is a detailed `dotnet build` log: ~1 MB of restore/progress noise around a handful of diagnostics,
// returned either as plain text or wrapped in build_verifier's JSON envelope.

using System.Globalization;
using System.Text;
using System.Text.Json;
using AgentOs.Domain.Tools;
using BenchmarkDotNet.Attributes;

namespace AgentOs.Benchmarks.Tools;

/// <summary><see cref="ToolOutputShaper.Shape"/> on a 1 MB failing build log, as text and as build_verifier JSON.</summary>
[MemoryDiagnoser]
public class ToolOutputShaperBenchmarks
{
    private string _buildLog = null!;
    private string _buildJson = null!;

    /// <summary>A ~1 MB detailed build: restore + per-project progress noise, repeated warnings, five errors, the summary.</summary>
    [GlobalSetup]
    public void Setup()
    {
        var sb = new StringBuilder(1_100_000);
        sb.Append("  Determining projects to restore...\n");
        var i = 0;
        while (sb.Length < 1_000_000)
        {
            sb.Append(CultureInfo.InvariantCulture, $"  Restored /work/src/Project{i % 40}/Project{i % 40}.csproj (in {i % 900} ms).\n");
            sb.Append(CultureInfo.InvariantCulture, $"  Project{i % 40} -> /work/src/Project{i % 40}/bin/Release/net10.0/Project{i % 40}.dll\n");
            if (i % 25 == 0)
            {
                sb.Append("/work/src/Core/Board.cs(12,17): warning CS0219: The variable 'unused' is assigned but its value is never used [/work/src/Core/Core.csproj]\n");
            }
            i++;
        }
        sb.Append("/work/src/Core/Rules.cs(4,35): error CS0029: Cannot implicitly convert type 'string' to 'int' [/work/src/Core/Core.csproj]\n");
        sb.Append("/work/src/Core/Rules.cs(4,50): error CS0103: The name 'y' does not exist in the current context [/work/src/Core/Core.csproj]\n");
        sb.Append("/work/src/Core/Rules.cs(5,29): error CS0103: The name 'Missing' does not exist in the current context [/work/src/Core/Core.csproj]\n");
        sb.Append("/work/src/Core/Rules.cs(7,37): error CS0029: Cannot implicitly convert type 'int' to 'string' [/work/src/Core/Core.csproj]\n");
        sb.Append("/work/src/Core/Rules.cs(7,49): error CS1061: 'string' does not contain a definition for 'Lenght' [/work/src/Core/Core.csproj]\n");
        sb.Append("\nBuild FAILED.\n\n    1 Warning(s)\n    5 Error(s)\n\nTime Elapsed 00:01:12.55\n");
        _buildLog = sb.ToString();
        _buildJson = JsonSerializer.Serialize(new { success = false, exitCode = 1, output = _buildLog });
    }

    /// <summary>The plain-text build log at the default budget.</summary>
    [Benchmark(Baseline = true)]
    public ShapedToolOutput ShapeBuildLog() => ToolOutputShaper.Shape(_buildLog, ToolOutputShaper.DefaultMaxOutputTokens);

    /// <summary>The same log inside a JSON envelope (the long string value gets the log pass in place).</summary>
    [Benchmark]
    public ShapedToolOutput ShapeBuildJson() => ToolOutputShaper.Shape(_buildJson, ToolOutputShaper.DefaultMaxOutputTokens);
}
