#!/usr/bin/env bash
# bundle.sh — offline (air-gapped) image bundle for an AgentOS.NET on-prem install (docs/ONPREM.md).
#
# On a machine WITH internet access (and the repository):
#   deploy/bundle.sh build                   # build agentos-net/{api,web,keycloak}:<version>
#   deploy/bundle.sh save                    # → dist/agentos-net-<version>.tar.gz (images + compose + chart + docs)
#   deploy/bundle.sh save --with-ollama      # … plus the Ollama image
#
# On the air-gapped host (only Docker needed — copy the tarball over, extract it, run the copy inside):
#   tar -xzf agentos-net-<version>.tar.gz && cd agentos-net-<version>
#   ./bundle.sh load                         # docker load the images
#   ./bundle.sh push registry.corp:5000      # optional: retag + push for Kubernetes (global.imageRegistry)
#
# Options: --version <v> (default: <Version> in Directory.Build.props, or the bundle's VERSION file),
#          --out <dir> (save; default dist/), --keycloak-image <ref> (build; a mirror of quay.io/keycloak/keycloak).
# Third-party image tags are read from compose/docker-compose.yml — the single source the Helm values match.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# Run from the repository (deploy/bundle.sh) or from an extracted bundle (./bundle.sh next to compose/).
if [[ -f "$here/../Directory.Build.props" ]]; then
  repo="$(cd "$here/.." && pwd)"
  compose_file="$here/compose/docker-compose.yml"
else
  repo=""
  compose_file="$here/compose/docker-compose.yml"
fi

cmd="${1:-}"; shift || true
version=""; out=""; with_ollama=false; keycloak_image=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --version) version="$2"; shift 2 ;;
    --out) out="$2"; shift 2 ;;
    --with-ollama) with_ollama=true; shift ;;
    --keycloak-image) keycloak_image="$2"; shift 2 ;;
    -h|--help) sed -n '2,19p' "${BASH_SOURCE[0]}"; exit 0 ;;
    *) registry="${registry:-$1}"; shift ;;
  esac
done

if [[ -z "$version" ]]; then
  if [[ -f "$here/VERSION" ]]; then
    version="$(cat "$here/VERSION")"
  elif [[ -n "$repo" ]]; then
    version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$repo/Directory.Build.props" | head -n1)"
  fi
fi
[[ -n "$version" ]] || { echo "Cannot determine the version — pass --version." >&2; exit 1; }

third_party() { # postgres:<tag> [ollama/ollama:<tag>] from the compose file
  local pg ol
  pg="$(sed -n 's/^ *image: \(postgres:[^ ]*\)$/\1/p' "$compose_file" | head -n1)"
  [[ -n "$pg" ]] || { echo "No postgres image in $compose_file" >&2; exit 1; }
  echo "$pg"
  if $with_ollama; then
    ol="$(sed -n 's/^ *image: \(ollama\/ollama:[^ ]*\)$/\1/p' "$compose_file" | head -n1)"
    [[ -n "$ol" ]] || { echo "No ollama image in $compose_file" >&2; exit 1; }
    echo "$ol"
  fi
}
own() { for c in api web keycloak; do echo "agentos-net/$c:$version"; done; }

case "$cmd" in
  build)
    [[ -n "$repo" ]] || { echo "build runs from the repository (deploy/bundle.sh)." >&2; exit 1; }
    docker build -t "agentos-net/api:$version" -f "$repo/Dockerfile" "$repo"
    docker build -t "agentos-net/web:$version" -f "$repo/Dockerfile.web" "$repo"
    kc_args=()
    [[ -n "$keycloak_image" ]] && kc_args=(--build-arg "KEYCLOAK_IMAGE=$keycloak_image")
    docker build "${kc_args[@]}" -t "agentos-net/keycloak:$version" "$repo/infra/keycloak"
    echo "Built: $(own | tr '\n' ' ')"
    ;;

  save)
    [[ -n "$repo" ]] || { echo "save runs from the repository (deploy/bundle.sh)." >&2; exit 1; }
    command -v helm >/dev/null || { echo "helm is required to package the chart." >&2; exit 1; }
    mapfile -t images < <(own; third_party)
    for img in "${images[@]}"; do
      docker image inspect "$img" >/dev/null 2>&1 || docker pull "$img"
    done
    out="${out:-$repo/dist}"
    name="agentos-net-$version"
    work="$(mktemp -d)"
    trap 'rm -rf "$work"' EXIT
    stage="$work/$name"
    mkdir -p "$stage/compose" "$stage/helm" "$stage/docs"
    echo "Saving ${#images[@]} images…"
    docker save "${images[@]}" | gzip > "$stage/images.tar.gz"
    printf '%s\n' "${images[@]}" > "$stage/IMAGES"
    echo "$version" > "$stage/VERSION"
    cp "$here/bundle.sh" "$stage/"
    cp "$here/compose/docker-compose.yml" "$here/compose/.env.example" "$here/compose/init-secrets.sh" "$stage/compose/"
    # Compose mounts the shared scripts from the chart directory — keep the same relative layout.
    mkdir -p "$stage/helm/agentos-net/files"
    cp "$here/helm/agentos-net/files/"*.sh "$stage/helm/agentos-net/files/"
    helm package "$here/helm/agentos-net" --app-version "$version" -d "$stage/helm" >/dev/null
    cp "$repo/docs/ONPREM.md" "$repo/docs/dataprotection-dr-runbook.md" "$stage/docs/"
    # The air-gapped compose host has no build context: pin the images to the bundled tag.
    sed -i "s/^AGENTOS_VERSION=.*/AGENTOS_VERSION=$version/" "$stage/compose/.env.example"
    (cd "$stage" && sha256sum images.tar.gz helm/*.tgz > SHA256SUMS)
    mkdir -p "$out"
    tar -C "$work" -czf "$out/$name.tar.gz" "$name"
    (cd "$out" && sha256sum "$name.tar.gz" > "$name.tar.gz.sha256")
    echo "Bundle: $out/$name.tar.gz ($(du -h "$out/$name.tar.gz" | cut -f1)) + .sha256"
    ;;

  load)
    archive="$here/images.tar.gz"
    [[ -f "$archive" ]] || { echo "No images.tar.gz next to bundle.sh — run load from an extracted bundle." >&2; exit 1; }
    if [[ -f "$here/SHA256SUMS" ]]; then (cd "$here" && sha256sum -c SHA256SUMS); fi
    gunzip -c "$archive" | docker load
    ;;

  push)
    [[ -n "${registry:-}" ]] || { echo "usage: bundle.sh push <registry[:port][/prefix]>" >&2; exit 1; }
    registry="${registry%/}"
    if [[ -f "$here/IMAGES" ]]; then mapfile -t images < "$here/IMAGES"; else mapfile -t images < <(own; third_party); fi
    for img in "${images[@]}"; do
      docker tag "$img" "$registry/$img"
      docker push "$registry/$img"
    done
    echo "Pushed to $registry — install the chart with --set global.imageRegistry=$registry"
    ;;

  *)
    sed -n '2,19p' "${BASH_SOURCE[0]}"
    exit 1
    ;;
esac
