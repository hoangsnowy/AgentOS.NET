// IBuildVerifier impl. Owns ADR-0005 Layer 1 (input hardening): writes the model's SOURCE files to a
// scratch dir (rejecting any build-control file), ALWAYS synthesizes its own zero-dependency project +
// a feed-clearing nuget.config, then hands the prepared dir to an ISandboxedBuildRunner (Layer 2) which
// owns *where* `dotnet build` executes — in-process (Dev) or in an ephemeral no-egress container (Prod).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentOs.Modules.Integration.Sandbox;
using AgentOs.Domain.Pipeline;
using Microsoft.Extensions.Logging;

namespace AgentOs.Modules.Integration;

/// <inheritdoc cref="IBuildVerifier"/>
public sealed class BuildVerifier : IBuildVerifier
{
    private readonly ILogger<BuildVerifier> _logger;
    private readonly IBuildVerifierOptionsSource _options;
    private readonly Func<BuildVerifierOptions, ISandboxedBuildRunner> _runnerFor;

    /// <summary>Initializes the verifier with a logger, a fixed enable/sandbox config, and the build runner.</summary>
    public BuildVerifier(ILogger<BuildVerifier> logger, BuildVerifierOptions options, ISandboxedBuildRunner runner)
        : this(logger, new StaticBuildVerifierOptionsSource(options ?? throw new ArgumentNullException(nameof(options))),
               _ => runner ?? throw new ArgumentNullException(nameof(runner)))
    {
    }

    /// <summary>Initializes the verifier with LIVE options (Settings → Platform → Build sandbox) and a runner
    /// factory: each build resolves the current gate/sandbox/quotas and gets a runner for that sandbox mode.</summary>
    public BuildVerifier(
        ILogger<BuildVerifier> logger, IBuildVerifierOptionsSource options, Func<BuildVerifierOptions, ISandboxedBuildRunner> runnerFor)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _runnerFor = runnerFor ?? throw new ArgumentNullException(nameof(runnerFor));
    }

    /// <inheritdoc />
    public Task<BuildVerifyResult> VerifyAsync(PipelineResult result, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);

        var files = new List<BuildVerifyFile>();
        if (result.Code?.Files is not null)
        {
            files.AddRange(result.Code.Files.Select(f => new BuildVerifyFile(f.Path, f.Content)));
        }
        if (result.Tests?.Files is not null)
        {
            files.AddRange(result.Tests.Files.Select(f => new BuildVerifyFile(f.Path, f.Content)));
        }
        return VerifyFilesAsync(files, ct);
    }

    /// <inheritdoc />
    public async Task<BuildVerifyResult> VerifyFilesAsync(IEnumerable<BuildVerifyFile> files, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(files);

        // Gate: refuse to run a build when disabled — the default outside Development until a sandboxed
        // runner (Sandbox=Container) is selected.
        var options = await _options.CurrentAsync(ct).ConfigureAwait(false);
        if (!options.Enabled)
        {
            _logger.LogWarning("Build verification is disabled on this host (Integration:BuildVerifier:Enabled=false); refusing to run `dotnet build`.");
            return new BuildVerifyResult(false, -1,
                "Build verification is disabled on this host. It runs untrusted generated code and is off by "
                + "default outside Development; enable Integration:BuildVerifier:Enabled only where builds are sandboxed.",
                0);
        }

        var workDir = Path.Join(Path.GetTempPath(), "agentic-sdlc-build-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(workDir);
        var workDirFull = Path.GetFullPath(workDir);
        _logger.LogInformation("Build verifier scratch dir: {Dir} (sandbox: {Sandbox})", workDir, options.Sandbox);

        try
        {
            var rejected = await PrepareWorkspaceAsync(files, workDir, workDirFull, ct).ConfigureAwait(false);

            // Layer 2: hand the prepared, sanitized dir to the configured isolation host.
            var result = await _runnerFor(options).RunAsync(workDir, ct).ConfigureAwait(false);

            if (rejected > 0)
            {
                // Tell the caller the model's build-control files were dropped, so a "build succeeded" is
                // never mistaken for "the model's exact project built".
                var notice = $"[build_verifier] Rejected {rejected} build-control file(s) "
                    + "(project/solution/import/config/response) — only source files are compiled, "
                    + "against a synthesized zero-dependency project.\n";
                return result with { Output = notice + result.Output };
            }
            return result;
        }
        finally
        {
            // Best-effort cleanup; don't surface errors here.
            try { Directory.Delete(workDir, recursive: true); }
            catch (IOException ex) { _logger.LogWarning(ex, "Failed to delete scratch dir {Dir}", workDir); }
            catch (UnauthorizedAccessException ex) { _logger.LogWarning(ex, "Failed to delete scratch dir {Dir}", workDir); }
        }
    }

    // ADR-0005 Layer 1. Writes the model's SOURCE files (build-control files rejected, paths confined to
    // the scratch dir), then ALWAYS synthesizes a zero-dependency project + a feed-clearing nuget.config.
    // Returns the number of rejected/skipped files.
    private async Task<int> PrepareWorkspaceAsync(
        IEnumerable<BuildVerifyFile> files, string workDir, string workDirFull, CancellationToken ct)
    {
        var rejected = 0;
        foreach (var file in files)
        {
            var path = file.Path;
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }
            // The model may contribute SOURCE files only. A project/solution/import/config/response file
            // lets it inject MSBuild tasks (RCE) or repoint restore at a hostile feed — drop it.
            if (BuildInputSanitizer.IsBuildControlFile(path))
            {
                rejected++;
                _logger.LogWarning("Rejected a generated build-control file (project/import/config) — only source files are compiled.");
                continue;
            }
            // Generated paths come from the LLM — normalise + confine to the scratch dir so a rooted or
            // ../-laden path cannot write outside the sandbox (path traversal).
            var dest = Path.GetFullPath(Path.Join(workDir, path));
            if (!dest.StartsWith(workDirFull + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                rejected++;
                _logger.LogWarning("Skipping a generated file whose path escapes the build sandbox.");
                continue;
            }
            var destDir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(destDir))
            {
                Directory.CreateDirectory(destDir);
            }
            await File.WriteAllTextAsync(dest, file.Content, ct).ConfigureAwait(false);
        }

        // ALWAYS synthesize the project — a single zero-dependency SDK-style net10.0 project (no
        // PackageReference/Analyzer/ProjectReference, so no build-time package or source generator can be
        // introduced). Any model-authored .csproj was rejected above.
        await File.WriteAllTextAsync(Path.Join(workDir, "AgentOsGenerated.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
              </PropertyGroup>
            </Project>
            """, ct).ConfigureAwait(false);

        // Cut the feed: a project-local nuget.config clearing all package sources, so even if something
        // triggers a restore it cannot reach a remote (or attacker-supplied) feed. A zero-dependency
        // net10.0 project resolves its framework packs from the SDK install, offline.
        await File.WriteAllTextAsync(Path.Join(workDir, "nuget.config"),
            """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
              </packageSources>
            </configuration>
            """, ct).ConfigureAwait(false);

        return rejected;
    }
}
