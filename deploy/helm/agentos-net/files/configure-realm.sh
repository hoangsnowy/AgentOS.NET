#!/usr/bin/env bash
# configure-realm.sh — bring the imported 'agentic' realm in line with an on-prem install.
#
# The on-prem twin of infra/hooks/postdeploy.sh (the azd hook), written against kcadm.sh so it runs INSIDE
# the Keycloak image itself — no curl/jq, nothing to download, so it works air-gapped. Used by:
#   • docker compose  → the one-shot `keycloak-config` service (deploy/compose/docker-compose.yml)
#   • Helm            → the post-install/post-upgrade Job (templates/keycloak-realm-job.yaml)
# Idempotent: safe on every start / upgrade.
#
# What it does (the realm import bakes dev defaults — https://localhost:5180 redirect URIs, the public
# `agentic-web-dev-secret`, operator/operator):
#   1. agentic-web client → redirectUris/webOrigins = WEB_PUBLIC_URL, secret = KEYCLOAK_WEB_CLIENT_SECRET.
#   2. verifyEmail on only when SMTP is configured (KC_SMTP_HOST), else off (signup would dead-end).
#   3. Ensures the platform-admin realm role (an existing realm is never re-imported).
#   4. Rotates the seed `operator` (first admin) to OPERATOR_PASSWORD — required, the import's is public.
#      Rotates `member` to MEMBER_PASSWORD, or DISABLES it when that is unset.
#
# Env: KEYCLOAK_URL (admin/back-channel URL, e.g. http://keycloak:8080), KEYCLOAK_ADMIN_USERNAME,
#      KEYCLOAK_ADMIN_PASSWORD, WEB_PUBLIC_URL, KEYCLOAK_WEB_CLIENT_SECRET, OPERATOR_PASSWORD,
#      MEMBER_PASSWORD (optional), KC_SMTP_HOST (optional), REALM (default agentic),
#      KEYCLOAK_WAIT_SECONDS (default 600).
set -euo pipefail

: "${KEYCLOAK_URL:?KEYCLOAK_URL is required}"
: "${KEYCLOAK_ADMIN_PASSWORD:?KEYCLOAK_ADMIN_PASSWORD is required}"
: "${WEB_PUBLIC_URL:?WEB_PUBLIC_URL is required}"
: "${KEYCLOAK_WEB_CLIENT_SECRET:?KEYCLOAK_WEB_CLIENT_SECRET is required}"
: "${OPERATOR_PASSWORD:?OPERATOR_PASSWORD is required — the imported seed password is public}"

KCADM=/opt/keycloak/bin/kcadm.sh
REALM="${REALM:-agentic}"
ADMIN_USER="${KEYCLOAK_ADMIN_USERNAME:-admin}"
WEB_URL="${WEB_PUBLIC_URL%/}"
WAIT="${KEYCLOAK_WAIT_SECONDS:-600}"
# kcadm keeps its session in a config file; keep it off the (possibly read-only) home directory.
CFG="$(mktemp -d)/kcadm.config"
kc() { "$KCADM" "$@" --config "$CFG"; }

echo "Waiting for Keycloak at $KEYCLOAK_URL (up to ${WAIT}s)…"
deadline=$((SECONDS + WAIT))
until kc config credentials --server "$KEYCLOAK_URL" --realm master \
        --user "$ADMIN_USER" --password "$KEYCLOAK_ADMIN_PASSWORD" >/dev/null 2>&1; do
  if (( SECONDS >= deadline )); then
    echo "ERROR: could not sign in to Keycloak as '$ADMIN_USER' within ${WAIT}s." >&2
    exit 1
  fi
  sleep 5
done

if ! kc get "realms/$REALM" --fields realm >/dev/null 2>&1; then
  echo "ERROR: realm '$REALM' does not exist — import infra/keycloak/agentic-realm.json first (see docs/ONPREM.md)." >&2
  exit 1
fi
echo "Configuring realm '$REALM' for $WEB_URL"

# 1. agentic-web client: redirect URIs, web origins, client secret.
client_id="$(kc get clients -r "$REALM" -q clientId=agentic-web --fields id --format csv --noquotes | head -n1)"
if [[ -z "$client_id" ]]; then
  echo "ERROR: client 'agentic-web' not found in realm '$REALM'." >&2
  exit 1
fi
kc update "clients/$client_id" -r "$REALM" \
  -s "redirectUris=[\"$WEB_URL/*\"]" \
  -s "webOrigins=[\"$WEB_URL\"]" \
  -s "secret=$KEYCLOAK_WEB_CLIENT_SECRET"
echo "  agentic-web: redirectUris=$WEB_URL/*, secret rotated"

# 2. Email verification only when an SMTP server is wired (the realm reads KC_SMTP_* at import time).
if [[ -n "${KC_SMTP_HOST:-}" ]]; then verify=true; else verify=false; fi
kc update "realms/$REALM" -s "verifyEmail=$verify"
echo "  verifyEmail=$verify"

# 3. platform-admin realm role (ADR-0011 platform-wide settings).
if kc get "roles/platform-admin" -r "$REALM" >/dev/null 2>&1; then
  echo "  role platform-admin present"
else
  kc create roles -r "$REALM" -s name=platform-admin \
    -s "description=Platform administrator - platform-wide settings for every tenant" >/dev/null
  echo "  role platform-admin created"
fi

# 4. Seed users.
user_id() { kc get users -r "$REALM" -q "username=$1" -q exact=true --fields id --format csv --noquotes | head -n1; }

op_id="$(user_id operator)"
if [[ -n "$op_id" ]]; then
  kc set-password -r "$REALM" --userid "$op_id" --new-password "$OPERATOR_PASSWORD"
  echo "  operator: password set"
else
  echo "  WARN: seed user 'operator' not found — create your first admin in the Keycloak admin console."
fi

member_id="$(user_id member)"
if [[ -n "$member_id" ]]; then
  if [[ -n "${MEMBER_PASSWORD:-}" ]]; then
    kc set-password -r "$REALM" --userid "$member_id" --new-password "$MEMBER_PASSWORD"
    kc update "users/$member_id" -r "$REALM" -s enabled=true
    echo "  member: password set"
  else
    kc update "users/$member_id" -r "$REALM" -s enabled=false
    echo "  member: disabled (MEMBER_PASSWORD not set)"
  fi
fi

echo "Realm '$REALM' configured."
