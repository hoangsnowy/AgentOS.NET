{{/* ---------- Names + labels ---------- */}}

{{- define "agentos.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "agentos.fullname" -}}
{{- if .Values.fullnameOverride -}}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" -}}
{{- else if contains (include "agentos.name" .) .Release.Name -}}
{{- .Release.Name | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- printf "%s-%s" .Release.Name (include "agentos.name" .) | trunc 63 | trimSuffix "-" -}}
{{- end -}}
{{- end -}}

{{/* "<fullname>-<component>", kept within the 63-char DNS label limit. */}}
{{- define "agentos.component" -}}
{{- printf "%s-%s" (include "agentos.fullname" .root | trunc (sub 62 (len .name) | int)) .name | trimSuffix "-" -}}
{{- end -}}

{{- define "agentos.labels" -}}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" }}
app.kubernetes.io/name: {{ include "agentos.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
app.kubernetes.io/part-of: agentos-net
{{- end -}}

{{- define "agentos.selectorLabels" -}}
app.kubernetes.io/name: {{ include "agentos.name" .root }}
app.kubernetes.io/instance: {{ .root.Release.Name }}
app.kubernetes.io/component: {{ .name }}
{{- end -}}

{{/* ---------- Images ---------- */}}

{{/* (dict "root" $ "image" .Values.web.image) → "[registry/]repository:tag" (tag defaults to appVersion). */}}
{{- define "agentos.image" -}}
{{- $registry := .root.Values.global.imageRegistry -}}
{{- $tag := .image.tag | default .root.Chart.AppVersion -}}
{{- if $registry -}}
{{- printf "%s/%s:%s" (trimSuffix "/" $registry) .image.repository $tag -}}
{{- else -}}
{{- printf "%s:%s" .image.repository $tag -}}
{{- end -}}
{{- end -}}

{{- define "agentos.imagePullSecrets" -}}
{{- with .Values.global.imagePullSecrets }}
imagePullSecrets:
{{- range . }}
  - name: {{ . }}
{{- end }}
{{- end }}
{{- end -}}

{{/* ---------- Public URLs ---------- */}}

{{- define "agentos.scheme" -}}
{{- if .Values.ingress.tls.enabled -}}https{{- else -}}http{{- end -}}
{{- end -}}

{{/* What the browser uses for the desktop. */}}
{{- define "agentos.webUrl" -}}
{{- if .Values.web.publicUrl -}}
{{- trimSuffix "/" .Values.web.publicUrl -}}
{{- else if and .Values.ingress.enabled .Values.ingress.hosts.web -}}
{{- printf "%s://%s" (include "agentos.scheme" .) .Values.ingress.hosts.web -}}
{{- else -}}
{{- fail "Set web.publicUrl, or ingress.enabled=true with ingress.hosts.web — the realm's redirect URIs are built from it." -}}
{{- end -}}
{{- end -}}

{{/* Keycloak's public URL (issuer + browser redirects). */}}
{{- define "agentos.keycloakUrl" -}}
{{- if .Values.keycloak.publicUrl -}}
{{- trimSuffix "/" .Values.keycloak.publicUrl -}}
{{- else if and .Values.keycloak.enabled .Values.ingress.enabled .Values.ingress.hosts.keycloak -}}
{{- printf "%s://%s" (include "agentos.scheme" .) .Values.ingress.hosts.keycloak -}}
{{- else -}}
{{- fail "Set keycloak.publicUrl (external Keycloak, or no ingress), or ingress.hosts.keycloak for the bundled one." -}}
{{- end -}}
{{- end -}}

{{/* Back channel the hosts + the realm Job use (discovery, token, JWKS, admin API). */}}
{{- define "agentos.keycloakInternalUrl" -}}
{{- if .Values.keycloak.internalUrl -}}
{{- trimSuffix "/" .Values.keycloak.internalUrl -}}
{{- else if .Values.keycloak.enabled -}}
{{- printf "http://%s:%d" (include "agentos.component" (dict "root" . "name" "keycloak")) (int .Values.keycloak.service.port) -}}
{{- else -}}
{{- include "agentos.keycloakUrl" . -}}
{{- end -}}
{{- end -}}

{{/* ---------- Database ---------- */}}

{{- define "agentos.db.host" -}}
{{- if .Values.postgresql.enabled -}}
{{- include "agentos.component" (dict "root" . "name" "postgres") -}}
{{- else -}}
{{- required "externalDatabase.host is required when postgresql.enabled=false" .Values.externalDatabase.host -}}
{{- end -}}
{{- end -}}

{{- define "agentos.db.port" -}}
{{- if .Values.postgresql.enabled -}}5432{{- else -}}{{ .Values.externalDatabase.port }}{{- end -}}
{{- end -}}

{{- define "agentos.db.name" -}}
{{- if .Values.postgresql.enabled -}}{{ .Values.postgresql.database }}{{- else -}}{{ .Values.externalDatabase.database }}{{- end -}}
{{- end -}}

{{- define "agentos.db.user" -}}
{{- if .Values.postgresql.enabled -}}{{ .Values.postgresql.username }}{{- else -}}{{ .Values.externalDatabase.username }}{{- end -}}
{{- end -}}

{{- define "agentos.db.keycloakName" -}}
{{- if .Values.postgresql.enabled -}}{{ .Values.postgresql.keycloakDatabase }}{{- else -}}{{ .Values.externalDatabase.keycloakDatabase }}{{- end -}}
{{- end -}}

{{/* Npgsql connection string; $(POSTGRES_PASSWORD) is expanded by the kubelet from the env var above it. */}}
{{- define "agentos.db.connectionString" -}}
{{- $s := printf "Host=%s;Port=%s;Database=%s;Username=%s;Password=$(POSTGRES_PASSWORD)" (include "agentos.db.host" .) (include "agentos.db.port" .) (include "agentos.db.name" .) (include "agentos.db.user" .) -}}
{{- if and (not .Values.postgresql.enabled) .Values.externalDatabase.sslMode -}}
{{- $s = printf "%s;SSL Mode=%s" $s .Values.externalDatabase.sslMode -}}
{{- end -}}
{{- $s -}}
{{- end -}}

{{- define "agentos.db.keycloakJdbcUrl" -}}
{{- $u := printf "jdbc:postgresql://%s:%s/%s" (include "agentos.db.host" .) (include "agentos.db.port" .) (include "agentos.db.keycloakName" .) -}}
{{- if and (not .Values.postgresql.enabled) .Values.externalDatabase.keycloakJdbcParams -}}
{{- $u = printf "%s?%s" $u .Values.externalDatabase.keycloakJdbcParams -}}
{{- end -}}
{{- $u -}}
{{- end -}}

{{/* ---------- Secrets ---------- */}}

{{- define "agentos.secretName" -}}
{{- .Values.secrets.existingSecret | default (include "agentos.component" (dict "root" . "name" "secrets")) -}}
{{- end -}}

{{/* (dict "root" $ "key" "postgres-password" "value" .Values.secrets.postgresPassword) → the explicit value, else
     the value already in the release's Secret (upgrades keep it), else a fresh random one.
     Generation needs `lookup`, which returns nothing when there is no cluster to ask: `helm template`, ArgoCD,
     Flux, `--dry-run=client`, helm-diff. Rendering there would mint NEW random passwords on every sync (and
     Postgres keeps the one baked at initdb) — so GitOps installs use secrets.existingSecret, or set
     secrets.generate=false to make the chart fail instead of generating. An upgrade whose release Secret
     cannot be found fails for the same reason rather than rotating every credential. */}}
{{- define "agentos.secretValue" -}}
{{- if .value -}}
{{- .value -}}
{{- else -}}
{{- $existing := lookup "v1" "Secret" .root.Release.Namespace (include "agentos.secretName" .root) -}}
{{- if and $existing (hasKey $existing.data .key) -}}
{{- index $existing.data .key | b64dec -}}
{{- else if not .root.Values.secrets.generate -}}
{{- fail (printf "secrets.generate=false: no value for Secret key %q — set secrets.existingSecret (required for GitOps / helm template renders) or the matching secrets.* value. See docs/ONPREM.md." .key) -}}
{{- else if and .root.Release.IsUpgrade (not $existing) -}}
{{- fail (printf "Upgrade of release %q: its Secret %s was not found (lookup returned nothing — an offline render such as --dry-run=client / helm-diff, or the Secret was deleted). Refusing to generate new credentials over the data they protect; restore the Secret or set secrets.existingSecret." .root.Release.Name (include "agentos.secretName" .root)) -}}
{{- else -}}
{{- randAlphaNum 32 -}}
{{- end -}}
{{- end -}}
{{- end -}}

{{/* The DataProtection certificate Secret to mount — empty when Azure Key Vault wraps the key ring instead
     (dataProtection.keyVaultKeyId), so a Key Vault install never waits on the default certificate Secret. */}}
{{- define "agentos.dpCertificateSecret" -}}
{{- if not .Values.dataProtection.keyVaultKeyId -}}
{{- .Values.dataProtection.certificate.existingSecret -}}
{{- end -}}
{{- end -}}

{{/* ---------- Pod hardening ---------- */}}

{{- define "agentos.containerSecurityContext" -}}
allowPrivilegeEscalation: false
capabilities:
  drop: ["ALL"]
seccompProfile:
  type: RuntimeDefault
{{- end -}}
