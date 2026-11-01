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
#   2. agentos-admin client → a confidential client whose service account holds ONLY the realm-management
#      roles the Api/Web need for member lifecycle (manage-users, view-users, query-users, view-realm);
#      secret = KEYCLOAK_ADMIN_CLIENT_SECRET. The hosts use it (client_credentials) instead of the master
#      admin, so the bootstrap admin can be rotated or disabled after setup.
#   3. verifyEmail on only when SMTP is configured (KC_SMTP_HOST), else off (signup would dead-end).
#   4. Ensures the platform-admin realm role (an existing realm is never re-imported).
#   5. FIRST RUN ONLY (realm attribute agentosSeedApplied): sets the seed `operator` (first admin) password to
#      OPERATOR_PASSWORD — required, the import's is public — and sets `member` to MEMBER_PASSWORD, or
#      DISABLES it when that is unset. Later runs never touch them again, so a password changed in Keycloak
#      is not reverted by the next `compose up` / `helm upgrade`.
#
# Sign-in: the master-realm bootstrap admin (KEYCLOAK_ADMIN_USERNAME/PASSWORD). If it no longer works (rotated
# or disabled after setup) but the agentos-admin service account does, the realm was configured before — the
# script logs a warning, changes nothing and exits 0. Provide a working master admin to re-apply a changed
# WEB_PUBLIC_URL or client secret.
#
# Env: KEYCLOAK_URL (admin/back-channel URL, e.g. http://keycloak:8080), KEYCLOAK_ADMIN_USERNAME,
#      KEYCLOAK_ADMIN_PASSWORD, WEB_PUBLIC_URL, KEYCLOAK_WEB_CLIENT_SECRET, KEYCLOAK_ADMIN_CLIENT_SECRET,
#      OPERATOR_PASSWORD, MEMBER_PASSWORD (optional), KC_SMTP_HOST (optional), REALM (default agentic),
#      ADMIN_CLIENT_ID (default agentos-admin), KEYCLOAK_WAIT_SECONDS (default 600).
set -euo pipefail

: "${KEYCLOAK_URL:?KEYCLOAK_URL is required}"
: "${KEYCLOAK_ADMIN_PASSWORD:?KEYCLOAK_ADMIN_PASSWORD is required}"
: "${WEB_PUBLIC_URL:?WEB_PUBLIC_URL is required}"
: "${KEYCLOAK_WEB_CLIENT_SECRET:?KEYCLOAK_WEB_CLIENT_SECRET is required}"
: "${KEYCLOAK_ADMIN_CLIENT_SECRET:?KEYCLOAK_ADMIN_CLIENT_SECRET is required}"
: "${OPERATOR_PASSWORD:?OPERATOR_PASSWORD is required — the imported seed password is public}"

# The templates ship every secret as CHANGE_ME for init-secrets.sh to fill in — never apply one.
for var in KEYCLOAK_ADMIN_PASSWORD KEYCLOAK_WEB_CLIENT_SECRET KEYCLOAK_ADMIN_CLIENT_SECRET OPERATOR_PASSWORD MEMBER_PASSWORD; do
  if [[ "${!var:-}" == *CHANGE_ME* ]]; then
    echo "ERROR: $var still holds the CHANGE_ME placeholder — run init-secrets.sh (compose) or set a real secret." >&2
    exit 1
  fi
done

KCADM=/opt/keycloak/bin/kcadm.sh
REALM="${REALM:-agentic}"
ADMIN_USER="${KEYCLOAK_ADMIN_USERNAME:-admin}"
ADMIN_CLIENT="${ADMIN_CLIENT_ID:-agentos-admin}"
ADMIN_CLIENT_ROLES=(manage-users view-users query-users view-realm)
SEED_MARKER=agentosSeedApplied
WEB_URL="${WEB_PUBLIC_URL%/}"
WAIT="${KEYCLOAK_WAIT_SECONDS:-600}"
# kcadm keeps its session in a config file; keep it off the (possibly read-only) home directory.
CFG="$(mktemp -d)/kcadm.config"
kc() { "$KCADM" "$@" --config "$CFG"; }

echo "Waiting for Keycloak at $KEYCLOAK_URL (up to ${WAIT}s)…"
deadline=$((SECONDS + WAIT))
until kc config credentials --server "$KEYCLOAK_URL" --realm master \
        --user "$ADMIN_USER" --password "$KEYCLOAK_ADMIN_PASSWORD" >/dev/null 2>&1; do
  # Bootstrap admin retired after setup? The service account proves the realm was configured before.
  if kc config credentials --server "$KEYCLOAK_URL" --realm "$REALM" \
        --client "$ADMIN_CLIENT" --secret "$KEYCLOAK_ADMIN_CLIENT_SECRET" >/dev/null 2>&1; then
    echo "WARN: master admin '$ADMIN_USER' cannot sign in, but the '$ADMIN_CLIENT' service account can — the realm"
    echo "      was configured before. Nothing changed. To re-apply WEB_PUBLIC_URL / client secrets, provide a"
    echo "      working master-realm admin in KEYCLOAK_ADMIN_USERNAME / KEYCLOAK_ADMIN_PASSWORD."
    exit 0
  fi
  if (( SECONDS >= deadline )); then
    echo "ERROR: could not sign in to Keycloak as '$ADMIN_USER' (or as the '$ADMIN_CLIENT' service account) within ${WAIT}s." >&2
    exit 1
  fi
  sleep 5
done

if ! kc get "realms/$REALM" --fields realm >/dev/null 2>&1; then
  echo "ERROR: realm '$REALM' does not exist — import infra/keycloak/agentic-realm.json first (see docs/ONPREM.md)." >&2
  exit 1
fi
echo "Configuring realm '$REALM' for $WEB_URL"

client_uuid() { kc get clients -r "$REALM" -q "clientId=$1" --fields id --format csv --noquotes | head -n1; }

# 1. agentic-web client: redirect URIs, web origins, client secret.
client_id="$(client_uuid agentic-web)"
if [[ -z "$client_id" ]]; then
  echo "ERROR: client 'agentic-web' not found in realm '$REALM'." >&2
  exit 1
fi
kc update "clients/$client_id" -r "$REALM" \
  -s "redirectUris=[\"$WEB_URL/*\"]" \
  -s "webOrigins=[\"$WEB_URL\"]" \
  -s "secret=$KEYCLOAK_WEB_CLIENT_SECRET"
echo "  agentic-web: redirectUris=$WEB_URL/*, secret rotated"

# 2. agentos-admin: service-account client for the hosts' member management (least privilege, no login flows).
admin_settings=(
  -s enabled=true -s publicClient=false -s serviceAccountsEnabled=true
  -s standardFlowEnabled=false -s implicitFlowEnabled=false -s directAccessGrantsEnabled=false
  -s "secret=$KEYCLOAK_ADMIN_CLIENT_SECRET"
  -s "description=AgentOS.NET Api/Web member management (service account)"
)
admin_id="$(client_uuid "$ADMIN_CLIENT")"
if [[ -n "$admin_id" ]]; then
  kc update "clients/$admin_id" -r "$REALM" "${admin_settings[@]}"
  echo "  $ADMIN_CLIENT: updated, secret rotated"
else
  kc create clients -r "$REALM" -s "clientId=$ADMIN_CLIENT" -s protocol=openid-connect "${admin_settings[@]}" >/dev/null
  echo "  $ADMIN_CLIENT: created"
fi
role_args=()
for r in "${ADMIN_CLIENT_ROLES[@]}"; do role_args+=(--rolename "$r"); done
kc add-roles -r "$REALM" --uusername "service-account-$ADMIN_CLIENT" --cclientid realm-management "${role_args[@]}"
echo "  $ADMIN_CLIENT: service account roles = realm-management/{${ADMIN_CLIENT_ROLES[*]}}"

# 3. Email verification only when an SMTP server is wired (the realm reads KC_SMTP_* at import time).
if [[ -n "${KC_SMTP_HOST:-}" ]]; then verify=true; else verify=false; fi
kc update "realms/$REALM" -s "verifyEmail=$verify"
echo "  verifyEmail=$verify"

# 4. platform-admin realm role (ADR-0011 platform-wide settings).
if kc get "roles/platform-admin" -r "$REALM" >/dev/null 2>&1; then
  echo "  role platform-admin present"
else
  kc create roles -r "$REALM" -s name=platform-admin \
    -s "description=Platform administrator - platform-wide settings for every tenant" >/dev/null
  echo "  role platform-admin created"
fi

# 5. Seed users — first run only. The marker is a realm attribute (free-form, unlike user attributes, which
#    the declarative user profile governs).
# (`--fields attributes` returns an empty map in kcadm 26 — read the whole representation.)
if kc get "realms/$REALM" | grep -Eq "\"$SEED_MARKER\"[[:space:]]*:[[:space:]]*\"true\""; then
  echo "  seed users: already initialised — passwords left as they are (change them in the Keycloak admin console)"
else
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

  kc update "realms/$REALM" -s "attributes.$SEED_MARKER=\"true\""
  echo "  seed users: initialised (never re-applied)"
fi

echo "Realm '$REALM' configured."
