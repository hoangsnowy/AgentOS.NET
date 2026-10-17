# ADR-0005 — `build_verifier` execution sandbox

- **Status:** Accepted (2026-06-18)
- **Deciders:** AgentOS maintainers
- **Context decision:** [D2](../../ROADMAP.md#4--open-architecture-decisions) — Q1 "Live on Azure"
- **Closes:** [deploy-readiness-audit #10](https://github.com/hoangsnowy/AgentOs/blob/90240386830067f52c07b1d781e223ccdfe5f174/docs/deploy-readiness-audit.md) (archived) (RCE via LLM-authored MSBuild)

## Context

The `build_verifier` tool answers "does this code compile?" by writing an LLM-generated file
set to a scratch directory and running `dotnet build`. **MSBuild executes arbitrary tasks and
targets during a build.** An LLM (or a prompt-injected one) can therefore emit:

- a `*.csproj` / `*.targets` / `*.props` / `Directory.Build.*` with `<Exec>`, `<UsingTask>`, or an
  inline `RoslynCodeTaskFactory` task → **arbitrary code execution at build time**;
- a `<PackageReference>` to a malicious package → restore + build-time tasks run that package's code;
- a `nuget.config` pointing restore at an attacker feed; a `.rsp` response file injecting build args.

Until now the tool ran MSBuild **in-process** on the server. The interim mitigation
([#72](https://github.com/hoangsnowy/AgentOs/pull/72)) gated it **off by default in Production**
(`Integration:BuildVerifier:Enabled`), which is safe but means the capability is simply unavailable
in the deployment that matters. Q1's exit criterion requires a *real* sandbox: a malicious-`.csproj`
fixture must prove the build cannot reach the host or the network.

## Decision

Defense in **two layers**, because the host-isolation layer alone is operationally heavy and the
input layer alone is the cheapest, most decisive cut of the RCE surface.

### Layer 1 — host-agnostic input hardening (applies on every host; shipped now)

1. **Reject build-control files outright.** The model may contribute *source* files only. Any
   project file (`*.csproj/.vbproj/.fsproj/.proj`), solution (`*.sln/.slnx`), MSBuild import
   (`*.targets/.props`), `Directory.Build.*`, `Directory.Packages.props`, `nuget.config`,
   `global.json`, or response file (`*.rsp`) is dropped and never written. This removes the
   arbitrary-MSBuild-task vector at the source.
2. **Always synthesize the project ourselves** — a single fixed SDK-style `net10.0` `.csproj` with
   **no** `PackageReference`/`Analyzer`/`ProjectReference`, so no source generator or build-time
   package code can be introduced.
3. **Cut the feed.** A synthesized `nuget.config` with `<clear />` (no remote sources); combined
   with a zero-dependency project, restore touches no network.
4. **Confine the filesystem.** Paths are normalised and confined to the scratch dir (existing
   traversal guard); `HOME`/`USERPROFILE` are redirected into the scratch dir so a build cannot read
   the server's user profile or NuGet credentials; telemetry/first-run/node-reuse/MSBuild-server are
   all off; output is byte-capped and the process is killed on a hard timeout.

### Layer 2 — OS isolation host (the ephemeral container; next slice)

Run the (already-hardened) build inside an **ephemeral, locked-down container** so even a defense
that slips through Layer 1 cannot touch the host or network:

- **Cloud host: Azure Container Apps Jobs** — one job execution per build, image is the
  build-runner, **egress disabled** (no outbound networking), CPU/memory/timeout quotas, non-root
  user, read-only root filesystem with a writable `tmpfs` scratch, `pids-limit`. The job is created
  and torn down per build.
- **Local/dev host: the same image under Docker** (`--network none --read-only --user`, `--cpus`,
  `--memory`, `--pids-limit`, `tmpfs`) so dev parity uses the identical sandbox.
- Behind an `ISandboxedBuildRunner` seam: `InProcess` (Layer-1-only, Development default) ·
  `Container` (Docker/ACA Jobs). `IToolGateway` governance (policy → invoke → evidence log) is
  unchanged and wraps either runner.

### Options considered for the isolation host (D2)

| Option | Verdict |
|---|---|
| **ACA Jobs** (chosen) | Native to our ACA deployment; per-exec lifecycle; egress + quotas first-class; no privileged daemon |
| Docker-in-ACA | Dead end — ACA does not support privileged/`dind` containers |
| ACI per-exec container group | Workable, but a second compute primitive to operate alongside ACA |
| Process-level jail only (no container) | Insufficient alone — shares host kernel/network/fs without strong isolation |

## Consequences

- **Layer 1 — shipped + unit-tested.** A `*.csproj`/`*.targets`/`*.props`/`nuget.config`/
  `Directory.Build.props`/`*.rsp` fixture is **rejected and never built**
  (`BuildInputSanitizerTests`); the synthesized project + cleared feed are always used;
  `HOME`/`USERPROFILE` are redirected (`InProcessBuildRunner`, not unit-asserted).
- **Layer 2 — shipped + locally E2E-verified.** `ISandboxedBuildRunner` with `InProcess` (Dev
  default) and `Container` runners; `ContainerCommand` composes the `docker run` argv
  (`ContainerCommandTests` assert the isolation flags). Verified on a real Docker host: a valid
  source set built **offline** (`--network none`), **non-root** (uid 1000), **read-only root fs**,
  `--cap-drop ALL`, `--security-opt no-new-privileges`, with CPU/memory/PID quotas → `Build
  succeeded`, exit 0. Selected via `Integration:BuildVerifier:Sandbox=Container`.
- **Remaining — user-driven cloud verify.** Deploy the same image as an **ACA Job** (egress disabled
  + quotas at the platform level) via bicep, and run the round-trip on a real `azd up`. The
  in-process build stays **gated off by default in Production**; flip it on only with
  `Sandbox=Container` + `Enabled=true` where the container host is present.
- **Trade-off:** rejecting model project files means `build_verifier` only ever compiles a flat
  source set against a fixed framework target — it cannot validate a build that genuinely needs a
  package. That is the correct trade for an untrusted-input compile-check; richer validation belongs
  to the repo-grounded execution path on a connected runner, not this tool.
