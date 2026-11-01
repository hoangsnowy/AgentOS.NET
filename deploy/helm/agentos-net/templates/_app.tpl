{{/*
Deployment + Service for one AgentOS.NET host (Api or Web): same image layout, env, probes and hardening.
Call with (dict "root" $ "name" "web" "values" .Values.web "extraEnv" (list ...)).
*/}}
{{- define "agentos.appWorkload" -}}
{{- $root := .root -}}
{{- $v := .values -}}
{{- $selector := dict "root" $root "name" .name -}}
{{- $secret := include "agentos.secretName" $root -}}
apiVersion: apps/v1
kind: Deployment
metadata:
  name: {{ include "agentos.component" $selector }}
  labels:
    {{- include "agentos.labels" $root | nindent 4 }}
    app.kubernetes.io/component: {{ .name }}
spec:
  replicas: {{ $v.replicaCount }}
  selector:
    matchLabels:
      {{- include "agentos.selectorLabels" $selector | nindent 6 }}
  template:
    metadata:
      labels:
        {{- include "agentos.selectorLabels" $selector | nindent 8 }}
      annotations:
        # Roll the pods when configuration changes.
        checksum/config: {{ include (print $root.Template.BasePath "/configmap.yaml") $root | sha256sum }}
        {{- if not $root.Values.secrets.existingSecret }}
        checksum/secret: {{ include (print $root.Template.BasePath "/secret.yaml") $root | sha256sum }}
        {{- end }}
    spec:
      {{- include "agentos.imagePullSecrets" $root | nindent 6 }}
      securityContext:
        {{- toYaml $root.Values.podSecurityContext | nindent 8 }}
      {{- if $root.Values.postgresql.enabled }}
      initContainers:
        # Wait for the bundled Postgres so the first boot's migrations don't race it (crash-loop otherwise).
        - name: wait-for-postgres
          image: {{ include "agentos.image" (dict "root" $root "image" $root.Values.postgresql.image) }}
          imagePullPolicy: {{ $root.Values.postgresql.image.pullPolicy }}
          command: ["sh", "-c", "until pg_isready -h \"$DB_HOST\" -p 5432; do sleep 2; done"]
          env:
            - name: DB_HOST
              value: {{ include "agentos.db.host" $root | quote }}
          securityContext:
            {{- include "agentos.containerSecurityContext" $root | nindent 12 }}
          resources:
            requests: { cpu: 10m, memory: 16Mi }
            limits: { memory: 64Mi }
      {{- end }}
      containers:
        - name: {{ .name }}
          image: {{ include "agentos.image" (dict "root" $root "image" $v.image) }}
          imagePullPolicy: {{ $v.image.pullPolicy }}
          ports:
            - name: http
              containerPort: 8080
          envFrom:
            - configMapRef:
                name: {{ include "agentos.component" (dict "root" $root "name" "config") }}
          env:
            - name: POSTGRES_PASSWORD
              valueFrom:
                secretKeyRef: { name: {{ $secret }}, key: postgres-password }
            - name: ConnectionStrings__DefaultConnection
              value: {{ include "agentos.db.connectionString" $root | quote }}
            - name: Auth__Keycloak__Admin__Password
              valueFrom:
                secretKeyRef: { name: {{ $secret }}, key: keycloak-admin-password }
            - name: DataProtection__CertificatePassword
              valueFrom:
                secretKeyRef: { name: {{ $secret }}, key: dataprotection-certificate-password, optional: true }
            {{- with .extraEnv }}
            {{- toYaml . | nindent 12 }}
            {{- end }}
            {{- with $v.extraEnv }}
            {{- toYaml . | nindent 12 }}
            {{- end }}
          startupProbe:
            httpGet: { path: {{ $root.Values.probes.startup.path }}, port: http }
            periodSeconds: {{ $root.Values.probes.startup.periodSeconds }}
            failureThreshold: {{ $root.Values.probes.startup.failureThreshold }}
          livenessProbe:
            httpGet: { path: {{ $root.Values.probes.liveness.path }}, port: http }
            periodSeconds: {{ $root.Values.probes.liveness.periodSeconds }}
            failureThreshold: {{ $root.Values.probes.liveness.failureThreshold }}
          readinessProbe:
            httpGet: { path: {{ $root.Values.probes.readiness.path }}, port: http }
            periodSeconds: {{ $root.Values.probes.readiness.periodSeconds }}
            failureThreshold: {{ $root.Values.probes.readiness.failureThreshold }}
            timeoutSeconds: 12
          securityContext:
            {{- include "agentos.containerSecurityContext" $root | nindent 12 }}
          resources:
            {{- toYaml $v.resources | nindent 12 }}
          {{- if $root.Values.dataProtection.certificate.existingSecret }}
          volumeMounts:
            - name: dataprotection
              mountPath: /app/dataprotection
              readOnly: true
          {{- end }}
      {{- if $root.Values.dataProtection.certificate.existingSecret }}
      volumes:
        - name: dataprotection
          secret:
            secretName: {{ $root.Values.dataProtection.certificate.existingSecret }}
            items:
              - key: {{ $root.Values.dataProtection.certificate.key }}
                path: {{ $root.Values.dataProtection.certificate.key }}
      {{- end }}
---
apiVersion: v1
kind: Service
metadata:
  name: {{ include "agentos.component" $selector }}
  labels:
    {{- include "agentos.labels" $root | nindent 4 }}
    app.kubernetes.io/component: {{ .name }}
spec:
  type: {{ $v.service.type }}
  selector:
    {{- include "agentos.selectorLabels" $selector | nindent 4 }}
  ports:
    - name: http
      port: {{ $v.service.port }}
      targetPort: http
{{- end -}}
