# Releases — versioning, cutting a release, verifying images

AgentOS.NET ships as two container images, **Api** and **Web**, built by
[`.github/workflows/release.yml`](../.github/workflows/release.yml) from a `vX.Y.Z` tag:

| Artifact | Where | Integrity |
|---|---|---|
| `ghcr.io/hoangsnowy/agentos-api:<version>` | GHCR | cosign keyless signature · SLSA build provenance · CycloneDX SBOM attestation |
| `ghcr.io/hoangsnowy/agentos-web:<version>` | GHCR | same |
| `agentos-net-<version>.solution.cdx.json` | GitHub Release asset | CycloneDX SBOM of the solution's NuGet graph (test projects excluded) |
| `agentos-{api,web}-<version>.image.cdx.json` | GitHub Release asset | CycloneDX SBOM of each image (OS packages + NuGet) |

The images are built the same way `azd` builds them for Azure Container Apps — SDK containers
(`dotnet publish -t:PublishContainer`, `EnableSdkContainerSupport` in the Api / Web csproj) on
`mcr.microsoft.com/dotnet/aspnet:10.0`, non-root (uid 1654), port 8080 — so a released image is what a
deploy runs. The `Dockerfile` / `Dockerfile.web` at the repo root are for local `docker build` only.

## Versioning (SemVer 2.0)

Tags are `vMAJOR.MINOR.PATCH`, optionally with a prerelease suffix (`v0.7.0-rc.1`). The tag **is** the
version: the workflow stamps it into every assembly (`/version` on both hosts) and into the image tags.

| Bump | When |
|---|---|
| **MAJOR** | A breaking change for an operator or integrator: a removed / renamed REST or MCP endpoint or field, a bootstrap configuration key that changes meaning, a migration that is not backwards-compatible with the previous minor, a removed setting. |
| **MINOR** | New capability (a `feat` commit), new settings, additive API, additive DB migrations. |
| **PATCH** | Fixes only (`fix`, `perf`, security patches); no new settings, no schema change beyond a fix. |

Before 1.0 the API is not frozen: a MINOR may carry breaking changes, and its CHANGELOG section then says so
under **Migration notes**. Prereleases (`-rc.N`, `-beta.N`) are published to GHCR and as GitHub prereleases
but never move the `X.Y` or `latest` tags.

Image tags per release: `X.Y.Z` always; `X.Y` for stable releases only; `latest` only for a stable release
that is at least the highest stable `v*` tag — a patch to an older line (`v0.6.3` after `v0.7.0`) moves
neither the `latest` image tag nor the GitHub "Latest release" badge. Pin deployments to a **digest**
(`…@sha256:…`), not a moving tag.

## Supported versions

| Line | Gets |
|---|---|
| Latest minor (`X.Y.*`) | Bug and security fixes, as patch releases. |
| Previous minor | Security fixes only, for **90 days** after the next minor ships. |
| Older | Unsupported — upgrade. |
| `main` | Always the development line; not a release. |

There is no LTS line before 1.0. After 1.0 the policy will add a designated LTS minor with 12 months of
security fixes; this page and [SECURITY.md](../SECURITY.md) will say which.

## Cutting a release

1. **Release PR** (Conventional Commit `chore(release): vX.Y.Z`):
   - Move the `## [Unreleased]` entries in [CHANGELOG.md](../CHANGELOG.md) under a new
     `## [X.Y.Z] - YYYY-MM-DD` heading, leave an empty `## [Unreleased]` above it, and update the compare
     links at the bottom.
   - Fill **Migration notes** for anything an operator must do (see the convention below).
   - Set `<Version>X.Y.Z</Version>` in [`Directory.Build.props`](../Directory.Build.props). A prerelease
     `v0.7.0-rc.1` keeps `<Version>` at the core `0.7.0` and gets its own `## [0.7.0-rc.1] - YYYY-MM-DD`
     section (the final release section then lists the full set of changes since the previous stable).
2. **Dry run** (optional, recommended): Actions → **Release** → *Run workflow* on the release branch. It
   builds, runs the tests, builds both images as local archives and produces all three SBOMs as workflow
   artifacts — nothing is pushed, signed or released.
3. **Merge**, then tag the merge commit on `main` and push the tag:

   ```bash
   git switch main && git pull
   git tag -a vX.Y.Z -m "AgentOS.NET X.Y.Z"
   git push origin vX.Y.Z
   ```

4. The workflow refuses a tag that does not point at a commit on `main`, a tag that is not SemVer, a tag
   whose core version differs from `Directory.Build.props`, or a version with no CHANGELOG section. Then it
   builds, tests, pushes, signs, attests and creates the GitHub Release with the CHANGELOG section as notes
   and the SBOMs attached.
5. **First release only:** GHCR packages start private. Under the repository's *Packages*, set
   `agentos-api` and `agentos-web` to **Public** (the images carry an `org.opencontainers.image.source`
   label, so they are already linked to the repository).
6. **Verify** one image as below and deploy by digest.

A bad release is never re-tagged: ship `X.Y.(Z+1)`. A tag that was pushed by mistake and has not
produced a release can be deleted and the workflow re-run.

### Who may tag (repository owner, one-time)

The signing identity is "`release.yml` running for `refs/tags/v…`" — it says *which workflow* signed, not
*who* pushed the tag or whether the commit was reviewed. The workflow already refuses a tag whose commit is
not on `main` (so only merged, reviewed code is ever signed), but anyone with write access can still push a
`v*` tag and trigger a release. Close that with a **tag ruleset**: repository *Settings → Rules → Rulesets →
New tag ruleset*, target `v*`, enforcement *Active*, rules **Restrict creations**, **Restrict updates** and
**Restrict deletions**, bypass list = the maintainers allowed to release.

## Verify an image

Resolve the digest once, and verify that — a tag can move, a digest can't:

```bash
IMAGE=ghcr.io/hoangsnowy/agentos-api
DIGEST=$(docker buildx imagetools inspect "$IMAGE:0.7.0" --format '{{json .Manifest}}' | jq -r .digest)
```

**Signature** ([cosign](https://docs.sigstore.dev/cosign/system_config/installation/) **v3+**): the keyless
certificate must name this repository's release workflow, running for a tag push. The workflow signs with
cosign v3, which writes the signature as a Sigstore bundle attached to the image as an OCI 1.1 referrer;
cosign v3 reads that by default. cosign 2.6 can verify it too with `--new-bundle-format=true`; older cosign
looks for the legacy `sha256-….sig` tag and reports no signatures.

```bash
cosign verify "$IMAGE@$DIGEST" \
  --certificate-identity-regexp '^https://github\.com/hoangsnowy/AgentOS\.NET/\.github/workflows/release\.yml@refs/tags/v' \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com \
  --certificate-github-workflow-trigger push
```

**Provenance + SBOM** ([GitHub CLI](https://cli.github.com/) 2.49+):

```bash
# SLSA build provenance (which commit, which workflow, which runner built it)
gh attestation verify "oci://$IMAGE@$DIGEST" --repo hoangsnowy/AgentOS.NET \
  --signer-workflow hoangsnowy/AgentOS.NET/.github/workflows/release.yml

# CycloneDX SBOM attestation
gh attestation verify "oci://$IMAGE@$DIGEST" --repo hoangsnowy/AgentOS.NET \
  --signer-workflow hoangsnowy/AgentOS.NET/.github/workflows/release.yml \
  --predicate-type https://cyclonedx.org/bom
```

Both attestations are also pushed to the registry next to the image, so an admission controller (Sigstore
policy-controller, Kyverno `verifyImages`, Ratify) can enforce the same identity at deploy time. The
GitHub Release assets carry the same SBOMs for scanners that read files (`grype sbom:agentos-api-….cdx.json`,
Dependency-Track).

## Migration notes convention

Every release section in CHANGELOG.md ends with `### Migration notes`. Write one bullet per action an
operator must take, in the order they take it, or `- None.` Cover at least:

- **Database** — each module applies its own EF migrations at startup (`InitializeModulesAsync`). Say which
  module gains a migration, whether it is long-running or locks a large table, and whether a rollback to the
  previous minor still works against the migrated schema.
- **Configuration** — new or changed bootstrap keys (`appsettings` / env, see [CONFIG.md](CONFIG.md));
  product settings live in the UI ([ADR-0011](adr/0011-ui-first-configuration.md)) and need a note only when a
  default changes behaviour.
- **Keycloak** — an existing realm is never re-imported: new roles, clients or mappers must be applied with
  the post-deploy hook or the admin API; say which.
- **API** — removed or deprecated REST / MCP endpoints and fields, with the replacement.
- **Paired runners** — when the runner (`AgentOs.RemoteAgent`, VS Code extension) must be updated with the
  server.

Example:

```markdown
### Migration notes

- Pipeline adds migration `AddRunIndexes` (online, < 1 s on 1 M runs); rolling back to 0.7.x is safe.
- New realm role `auditor`: run `infra/hooks/postdeploy.sh` (or create it in the Keycloak admin console).
```
