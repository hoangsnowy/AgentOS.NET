#!/usr/bin/env bash
# init-secrets.sh — fill deploy/compose/.env with random secrets and create the DataProtection certificate.
#
#   cp .env.example .env && ./init-secrets.sh
#
# Replaces every `=CHANGE_ME` value in .env (existing values are kept, so re-running is safe) and, when
# missing, writes dataprotection/dataprotection.pfx — a self-signed RSA certificate that wraps the
# DataProtection key ring at rest. Needs: openssl. Prints nothing secret.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
env_file="${1:-$here/.env}"
cert_dir="$here/dataprotection"

[[ -f "$env_file" ]] || { echo "No $env_file — run: cp .env.example .env" >&2; exit 1; }
command -v openssl >/dev/null || { echo "openssl is required" >&2; exit 1; }

# Hex secrets: no characters that need quoting in .env, JDBC URLs, connection strings or kcadm arguments.
rand() { openssl rand -hex 24; }

tmp="$(mktemp)"
trap 'rm -f "$tmp"' EXIT
while IFS= read -r line || [[ -n "$line" ]]; do
  if [[ "$line" =~ ^([A-Z0-9_]+)=CHANGE_ME$ ]]; then
    printf '%s=%s\n' "${BASH_REMATCH[1]}" "$(rand)"
  else
    printf '%s\n' "$line"
  fi
done < "$env_file" > "$tmp"
cat "$tmp" > "$env_file"
chmod 600 "$env_file"
echo "Secrets written to $env_file (mode 600)."

pfx="$cert_dir/dataprotection.pfx"
if [[ -f "$pfx" ]]; then
  echo "Keeping existing $pfx (never replace it while the database holds keys it wrapped)."
else
  password="$(sed -n 's/^DATAPROTECTION_CERTIFICATE_PASSWORD=//p' "$env_file")"
  [[ -n "$password" ]] || { echo "DATAPROTECTION_CERTIFICATE_PASSWORD is empty in $env_file" >&2; exit 1; }
  mkdir -p "$cert_dir"
  work="$(mktemp -d)"
  openssl req -x509 -newkey rsa:3072 -nodes -days 3650 -subj "/CN=AgentOS.NET DataProtection" \
    -keyout "$work/key.pem" -out "$work/cert.pem" 2>/dev/null
  openssl pkcs12 -export -inkey "$work/key.pem" -in "$work/cert.pem" -out "$pfx" \
    -passout "pass:$password" -name "AgentOS.NET DataProtection"
  rm -rf "$work"
  # The hosts run as the non-root `app` user (uid 1654) and mount the directory read-only: give the file to
  # that uid when we can (root/sudo), otherwise it has to stay world-readable for the container to load it.
  if chown 1654:1654 "$pfx" 2>/dev/null; then
    chmod 600 "$pfx"
  else
    chmod 644 "$pfx"
    echo "NOTE: could not chown $pfx to uid 1654 (not root) — left mode 644; restrict the directory instead."
  fi
  echo "DataProtection certificate written to $pfx — back it up SEPARATELY from the database."
fi
