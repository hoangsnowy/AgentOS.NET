# postdeploy.ps1 — run by `azd` after `azd deploy` (Windows; POSIX runs postdeploy.sh).
#
# Runs on POSTDEPLOY (not postprovision) so the Keycloak container app is actually up. Brings the
# imported 'agentic' realm in line with the cloud deployment:
#   1. Patches the 'agentic-web' client's redirectUris + webOrigins to the real ACA Web FQDN
#      (the realm import hardcodes https://localhost:5180) and rotates the client secret to the
#      KeycloakWebClientSecret azd parameter (the import bakes the public dev secret; the Web app
#      already receives the azd value, so without this patch every login fails on secret mismatch).
#   2. Disables realm verifyEmail in cloud — no SMTP wired by default.
#   3. Rotates the imported seed users' passwords (operator/member) to OPERATORPASSWORD /
#      MEMBERPASSWORD when set — the import ships public placeholder passwords.
#
# URLs are derived from the ACA environment default domain (azd output) — no manual base-URL step.
# Run manually:  pwsh infra/hooks/postdeploy.ps1
$ErrorActionPreference = 'Stop'

$domain      = $env:AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN
$kcUrl       = if ($env:KEYCLOAK_BASE_URL) { $env:KEYCLOAK_BASE_URL } elseif ($domain) { "https://keycloak.$domain" } else { $null }
$webUrl      = if ($env:WEB_BASE_URL) { $env:WEB_BASE_URL } elseif ($domain) { "https://web.$domain" } else { $null }
$adminPass   = if ($env:KeycloakAdminPassword) { $env:KeycloakAdminPassword } else { $env:KEYCLOAKADMINPASSWORD }
$adminUser   = if ($env:KeycloakAdminUsername) { $env:KeycloakAdminUsername } elseif ($env:KEYCLOAKADMINUSERNAME) { $env:KEYCLOAKADMINUSERNAME } else { 'admin' }
$webSecret   = if ($env:KeycloakWebClientSecret) { $env:KeycloakWebClientSecret } else { $env:KEYCLOAKWEBCLIENTSECRET }
$realm       = 'agentic'
$clientId    = 'agentic-web'

if (-not $kcUrl -or -not $webUrl -or -not $adminPass) {
    Write-Host "Skipping KC realm patch — could not resolve KC/Web URL (AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN) or KeycloakAdminPassword."
    exit 0
}

Write-Host "Patching realm '$realm' for $webUrl"

# Admin token — retry while Keycloak finishes starting (JVM warmup can 503 right after deploy).
$token = $null
for ($i = 0; $i -lt 12 -and -not $token; $i++) {
    try {
        $resp = Invoke-RestMethod -Method Post -Uri "$kcUrl/realms/master/protocol/openid-connect/token" `
            -ContentType 'application/x-www-form-urlencoded' `
            -Body @{ grant_type = 'password'; client_id = 'admin-cli'; username = $adminUser; password = $adminPass }
        $token = $resp.access_token
    } catch { Start-Sleep -Seconds 8 }
}
if (-not $token) { Write-Warning "Failed to get KC admin token after retries — skipping realm patch."; exit 0 }

$headers = @{ Authorization = "Bearer $token" }

# 1. agentic-web client: redirect URIs + origins + secret
$clients = Invoke-RestMethod -Headers $headers -Uri "$kcUrl/admin/realms/$realm/clients?clientId=$clientId"
$clientUuid = $clients[0].id
if (-not $clientUuid) { Write-Error "Client '$clientId' not found in realm '$realm'."; exit 1 }

$clientPatch = @{ redirectUris = @("$webUrl/*"); webOrigins = @($webUrl) }
if ($webSecret) { $clientPatch.secret = $webSecret }
Invoke-RestMethod -Method Put -Headers $headers -Uri "$kcUrl/admin/realms/$realm/clients/$clientUuid" `
    -ContentType 'application/json' -Body ($clientPatch | ConvertTo-Json) | Out-Null
Write-Host "Client patched: redirectUris=$webUrl/* secret=$(if ($webSecret) { 'rotated' } else { 'unchanged' })"

# 2. Realm: verifyEmail off in cloud (no SMTP by default)
try {
    Invoke-RestMethod -Method Put -Headers $headers -Uri "$kcUrl/admin/realms/$realm" `
        -ContentType 'application/json' -Body (@{ realm = $realm; verifyEmail = $false } | ConvertTo-Json) | Out-Null
    Write-Host "Realm verifyEmail=false"
} catch { Write-Warning "Failed to set verifyEmail=false: $_" }

# 2b. Realm role platform-admin (ADR-0011 platform-wide settings). Realm import is skipped once the realm
#     exists, so ensure the role here — idempotent (409 = already there). Assign it to the platform owner by
#     hand; it is never auto-granted in the cloud.
try {
    Invoke-RestMethod -Method Post -Headers $headers -Uri "$kcUrl/admin/realms/$realm/roles" -ContentType 'application/json' `
        -Body (@{ name = 'platform-admin'; description = 'Platform administrator - platform-wide settings for every tenant' } | ConvertTo-Json) | Out-Null
    Write-Host "Realm role platform-admin created."
} catch {
    if ($_.Exception.Response -and [int]$_.Exception.Response.StatusCode -eq 409) { Write-Host "Realm role platform-admin already present." }
    else { Write-Warning "Could not ensure realm role platform-admin: $($_.Exception.Message)" }
}

# 3. Seed users: rotate imported placeholder passwords
function Rotate-SeedUser([string]$username, [string]$newPass) {
    if (-not $newPass) {
        Write-Warning "$($username.ToUpper())PASSWORD not set — seed user '$username' keeps the public imported password."
        return
    }
    $users = Invoke-RestMethod -Headers $headers -Uri "$kcUrl/admin/realms/$realm/users?username=$username&exact=true"
    if (-not $users -or -not $users[0].id) { Write-Warning "Seed user '$username' not found — skipping."; return }
    try {
        Invoke-RestMethod -Method Put -Headers $headers -Uri "$kcUrl/admin/realms/$realm/users/$($users[0].id)/reset-password" `
            -ContentType 'application/json' -Body (@{ type = 'password'; value = $newPass; temporary = $false } | ConvertTo-Json) | Out-Null
        Write-Host "Seed user '$username' password rotated."
    } catch { Write-Warning "Failed to rotate '$username' password: $_" }
}

Rotate-SeedUser 'operator' $env:OPERATORPASSWORD
Rotate-SeedUser 'member'   $env:MEMBERPASSWORD

Write-Host "Realm patch complete."
