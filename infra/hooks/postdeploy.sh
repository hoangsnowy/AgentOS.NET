#!/usr/bin/env bash
# postdeploy.sh — run by `azd` after `azd deploy` (Windows runs postdeploy.ps1).
#
# Runs on POSTDEPLOY (not postprovision) so the Keycloak container app is actually up. Brings the
# imported 'agentic' realm in line with the cloud deployment:
#   1. Patches 'agentic-web' redirectUris + webOrigins to the real ACA Web FQDN (the import hardcodes
#      https://localhost:5180) and rotates the client secret to the KeycloakWebClientSecret azd
#      parameter (the import bakes the public dev secret; without this every login fails on mismatch).
#   2. Disables realm verifyEmail in cloud — no SMTP wired by default.
#   3. Rotates seed users (operator/member) to OPERATORPASSWORD / MEMBERPASSWORD when set.
#
# URLs are derived from the ACA environment default domain (azd output) — no manual base-URL step.
# Run manually:  bash infra/hooks/postdeploy.sh
set -euo pipefail

DOMAIN="${AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN:-}"
KC_URL="${KEYCLOAK_BASE_URL:-${DOMAIN:+https://keycloak.$DOMAIN}}"
WEB_URL="${WEB_BASE_URL:-${DOMAIN:+https://web.$DOMAIN}}"
KC_ADMIN_PASS="${KeycloakAdminPassword:-${KEYCLOAKADMINPASSWORD:-}}"
KC_ADMIN_USER="${KeycloakAdminUsername:-${KEYCLOAKADMINUSERNAME:-admin}}"
KC_WEB_SECRET="${KeycloakWebClientSecret:-${KEYCLOAKWEBCLIENTSECRET:-}}"
REALM="agentic"
CLIENT_ID="agentic-web"

if [[ -z "$KC_URL" || -z "$WEB_URL" || -z "$KC_ADMIN_PASS" ]]; then
  echo "Skipping KC realm patch — could not resolve KC/Web URL or KeycloakAdminPassword."
  exit 0
fi

echo "Patching realm '$REALM' for $WEB_URL"

# Admin token — retry while Keycloak finishes starting (JVM warmup can 503 right after deploy).
TOKEN=""
for i in $(seq 1 12); do
  TOKEN=$(curl -sf -X POST "$KC_URL/realms/master/protocol/openid-connect/token" \
    -d "grant_type=password" -d "client_id=admin-cli" \
    -d "username=$KC_ADMIN_USER" -d "password=$KC_ADMIN_PASS" 2>/dev/null | jq -r '.access_token // empty' || true)
  [[ -n "$TOKEN" ]] && break
  sleep 8
done
if [[ -z "$TOKEN" ]]; then echo "WARN: Failed to get KC admin token after retries — skipping realm patch."; exit 0; fi

auth=(-H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json")

# 1. agentic-web client: redirect URIs + origins + secret
CLIENT_UUID=$(curl -sf "${auth[@]}" "$KC_URL/admin/realms/$REALM/clients?clientId=$CLIENT_ID" | jq -r '.[0].id')
if [[ -z "$CLIENT_UUID" || "$CLIENT_UUID" == "null" ]]; then echo "ERROR: Client '$CLIENT_ID' not found."; exit 1; fi

CLIENT_PATCH="{\"redirectUris\":[\"$WEB_URL/*\"],\"webOrigins\":[\"$WEB_URL\"]"
if [[ -n "$KC_WEB_SECRET" ]]; then CLIENT_PATCH+=",\"secret\":$(jq -Rn --arg s "$KC_WEB_SECRET" '$s')"; fi
CLIENT_PATCH+="}"
curl -sf -X PUT "${auth[@]}" "$KC_URL/admin/realms/$REALM/clients/$CLIENT_UUID" -d "$CLIENT_PATCH" \
  && echo "Client patched: redirectUris=$WEB_URL/* secret=$([[ -n "$KC_WEB_SECRET" ]] && echo rotated || echo unchanged)" \
  || { echo "ERROR: Failed to patch KC client."; exit 1; }

# 2. Realm: verifyEmail off in cloud
curl -sf -X PUT "${auth[@]}" "$KC_URL/admin/realms/$REALM" -d '{"realm":"agentic","verifyEmail":false}' \
  && echo "Realm verifyEmail=false" || echo "WARN: Failed to set verifyEmail=false."

# 2b. Realm role platform-admin (ADR-0011 platform-wide settings). Realm import is skipped once the realm
#     exists, so ensure the role here — idempotent (409 = already there). Assign it to the platform owner by
#     hand; it is never auto-granted in the cloud.
code=$(curl -s -o /dev/null -w "%{http_code}" -X POST "${auth[@]}" "$KC_URL/admin/realms/$REALM/roles"   -d '{"name":"platform-admin","description":"Platform administrator - platform-wide settings for every tenant"}')
case "$code" in
  201) echo "Realm role platform-admin created." ;;
  409) echo "Realm role platform-admin already present." ;;
  *)   echo "WARN: Could not ensure realm role platform-admin (HTTP $code)." ;;
esac

# 3. Seed users: rotate imported placeholder passwords
rotate_user() {
  local username="$1" newpass="$2"
  [[ -z "$newpass" ]] && { echo "WARN: password for '$username' not set — keeps the public imported password."; return 0; }
  local uid
  uid=$(curl -sf "${auth[@]}" "$KC_URL/admin/realms/$REALM/users?username=$username&exact=true" | jq -r '.[0].id')
  [[ -z "$uid" || "$uid" == "null" ]] && { echo "WARN: seed user '$username' not found — skipping."; return 0; }
  curl -sf -X PUT "${auth[@]}" "$KC_URL/admin/realms/$REALM/users/$uid/reset-password" \
    -d "$(jq -n --arg p "$newpass" '{type:"password",value:$p,temporary:false}')" \
    && echo "Seed user '$username' password rotated." || echo "WARN: Failed to rotate '$username'."
}
rotate_user "operator" "${OPERATORPASSWORD:-}"
rotate_user "member"   "${MEMBERPASSWORD:-}"

echo "Realm patch complete."
