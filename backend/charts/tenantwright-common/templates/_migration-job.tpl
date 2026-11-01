{{/*
  tenantwrightcommon.migrationJob

  THE single definition of the one-shot schema migration Job. Every service chart
  includes this; none carries its own copy. That is the point: 19 hand-copied
  templates drift, and the failure mode is ugly (a Deployment gains an env var,
  the Job does not, and migrations start failing at startup with an error that
  looks nothing like a migration problem).

  What it does
    Runs the SAME service image with the SCHEMA-OWNER credential and
    EfCore__MigrateAndExit=true, so it migrates and terminates. The Deployment
    then runs with the runtime credential, which holds DML rights only and never
    needs DDL. Without this split, AutoMigrate on the Deployment would require
    granting every pod permission to alter or drop tables.

    As a pre-install/pre-upgrade hook it also runs exactly once, before any pod.
    AutoMigrate on the Deployment instead has every replica race to migrate on
    startup.

    Messaging is disabled here (Messaging__Provider=None). Migrating needs no broker, and a provider left
    on would make schema changes depend on RabbitMQ and its mTLS material first.

  Call it with a dict:
    root       required. the chart context (.)
    fullname   required. the release-scoped resource name
    standalone optional. true for charts with no _helpers.tpl and no secretEnv,
               which build env from a plain .Values.env map and mount named
               trust secrets directly.

  Example (helper-based chart):
    {{- include "tenantwrightcommon.migrationJob" (dict "root" . "fullname" (include "tenantwrightapp.fullname" .)) }}

  Example (standalone chart):
    {{- include "tenantwrightcommon.migrationJob" (dict "root" . "fullname" .Values.fullnameOverride "standalone" true) }}
*/}}
{{- define "tenantwrightcommon.migrationJob" -}}
{{- $root := .root -}}
{{- $fullname := required "tenantwrightcommon.migrationJob requires a fullname" .fullname -}}
{{- $standalone := .standalone | default false -}}
{{- $v := $root.Values -}}
{{- if and $v.migrationJob $v.migrationJob.enabled -}}
{{- /*
  Defaults live HERE, not repeated in 19 values.yaml files. Only schemaSecretName
  genuinely differs per service, so that is all a chart has to declare. A chart may
  still override any of these locally if it ever needs to.
*/ -}}
{{- $backoffLimit := $v.migrationJob.backoffLimit | default 1 -}}
{{- $deadline := $v.migrationJob.activeDeadlineSeconds | default 900 -}}
{{- $schemaSecretKey := $v.migrationJob.schemaSecretKey | default "connection-string" -}}
{{- $migrationEnv := $v.migrationJob.env | default dict -}}
{{- $ingressHost := "" -}}
{{- with $v.ingress -}}
{{- $ingressHost = .host | default "" -}}
{{- end -}}
{{- $resources := $v.migrationJob.resources | default (dict
      "requests" (dict "cpu" "50m"  "memory" "256Mi")
      "limits"   (dict "cpu" "500m" "memory" "512Mi")) -}}
apiVersion: batch/v1
kind: Job
metadata:
  name: {{ $fullname }}-migrate
  {{- if $standalone }}
  labels: { app: {{ $fullname }}, component: migration }
  {{- else }}
  labels:
    {{- include "tenantwrightapp.labels" $root | nindent 4 }}
    app.kubernetes.io/component: migration
  {{- end }}
  annotations:
    "helm.sh/hook": pre-install,pre-upgrade
    "helm.sh/hook-weight": "-5"
    "helm.sh/hook-delete-policy": before-hook-creation,hook-succeeded
spec:
  backoffLimit: {{ $backoffLimit }}
  {{- /*
    Bounded on purpose. If the process ever fails to shut down after migrating,
    the Job fails at the deadline instead of hanging the release indefinitely.
  */}}
  activeDeadlineSeconds: {{ $deadline }}
  template:
    metadata:
      {{- if $standalone }}
      labels: { app: {{ $fullname }}, component: migration }
      {{- else }}
      labels:
        {{- include "tenantwrightapp.selectorLabels" $root | nindent 8 }}
        app.kubernetes.io/component: migration
      {{- end }}
    spec:
      restartPolicy: Never
      # Migration hooks run before ordinary chart resources (including the
      # application ServiceAccount) are created.  The hook does not call the
      # Kubernetes API and has token mounting disabled, so use the namespace's
      # guaranteed default account instead of a release-owned account that may
      # not exist yet.
      serviceAccountName: default
      automountServiceAccountToken: false
      {{- if $standalone }}
      securityContext:
        runAsNonRoot: true
        fsGroup: 1000
        fsGroupChangePolicy: OnRootMismatch
        seccompProfile: { type: RuntimeDefault }
      {{- else }}
      {{- with $v.podSecurityContext }}
      securityContext:
        {{- toYaml . | nindent 8 }}
      {{- end }}
      {{- end }}
      containers:
        - name: migrate
          image: "{{ $v.image.repository }}{{- if $v.image.digest }}@{{ $v.image.digest }}{{- else }}:{{ $v.image.tag }}{{- end }}"
          imagePullPolicy: {{ $v.image.pullPolicy }}
          {{- if $standalone }}
          securityContext:
            allowPrivilegeEscalation: false
            readOnlyRootFilesystem: true
            capabilities: { drop: [ALL] }
          {{- else }}
          {{- with $v.securityContext }}
          securityContext:
            {{- toYaml . | nindent 12 }}
          {{- end }}
          {{- end }}
          {{- if not $standalone }}
          envFrom:
          {{- if $v.secretEnv.existingSecret }}
            - secretRef:
                name: {{ $v.secretEnv.existingSecret }}
          {{- else if and $v.secretEnv.create $v.secretEnv.stringData }}
            - secretRef:
                name: {{ include "tenantwrightapp.secretEnvName" $root }}
          {{- end }}
          {{- end }}
          env:
            {{- /*
              A pre-install hook cannot reference the release ConfigMap because
              Helm creates ordinary resources only after pre-install hooks have
              completed. Render the same non-secret settings directly so the
              migrator remains self-contained and still exercises startup
              options validation before touching the schema.
            */}}
            - name: AllowedHosts
              value: {{ printf "%s;%s" $ingressHost $fullname | quote }}
            {{- range $name, $value := $v.env }}
            {{- if and (not (has $name (list "AllowedHosts" "ConnectionStrings__Default" "EfCore__AutoMigrate" "EfCore__MigrateAndExit" "Messaging__Provider" "BackgroundTasks__Hangfire__PrepareSchemaIfNecessary" "PostgreSql__Runtime__ClientPool__MaximumPoolSize"))) (not (hasKey $migrationEnv $name)) }}
            - name: {{ $name }}
              value: {{ $value | toString | quote }}
            {{- end }}
            {{- end }}
            {{- with $v.config }}
            {{- with .forwardedHeaders }}
            {{- range $index, $value := .knownNetworks }}
            - name: ForwardedHeaders__KnownNetworks__{{ $index }}
              value: {{ $value | toString | quote }}
            {{- end }}
            {{- range $index, $value := .knownProxies }}
            - name: ForwardedHeaders__KnownProxies__{{ $index }}
              value: {{ $value | toString | quote }}
            {{- end }}
            {{- end }}
            {{- end }}
            {{- if $standalone }}
            {{- else }}
            {{- $hasSecretEnvMap := and $v.secretEnv.keys (or $v.secretEnv.existingSecret (and $v.secretEnv.create $v.secretEnv.stringData)) }}
            {{- if $hasSecretEnvMap }}
            {{- $secretName := $v.secretEnv.existingSecret | default (include "tenantwrightapp.secretEnvName" $root) }}
            {{- range $envName, $secretKey := $v.secretEnv.keys }}
            - name: {{ $envName }}
              valueFrom:
                secretKeyRef:
                  name: {{ $secretName }}
                  key: {{ $secretKey }}
            {{- end }}
            {{- end }}
            {{- end }}
            {{- /*
              Every secretRef the Deployment gets EXCEPT the runtime connection
              string, which is replaced below with the schema-owner credential.
              Keeping the rest matters: the host eagerly validates Valkey, audit
              signing and auth options at startup, so a trimmed env would fail
              before the migrator ever runs.
            */}}
            {{- range $v.secretRefs }}
            {{- if ne .envName "ConnectionStrings__Default" }}
            - name: {{ .envName }}
              valueFrom:
                secretKeyRef:
                  name: {{ .secretName }}
                  key: {{ .key }}
                  {{- if hasKey . "optional" }}
                  optional: {{ .optional }}
                  {{- end }}
            {{- end }}
            {{- end }}
            - name: ConnectionStrings__Default
              valueFrom:
                secretKeyRef:
                  name: {{ required "migrationJob.schemaSecretName is required when migrationJob.enabled=true" $v.migrationJob.schemaSecretName | quote }}
                  key: {{ $schemaSecretKey | quote }}
            - name: EfCore__AutoMigrate
              value: "true"
            - name: EfCore__MigrateAndExit
              value: "true"
            - name: Messaging__Provider
              value: "None"
            - name: PostgreSql__Runtime__ClientPool__MaximumPoolSize
              value: {{ $v.migrationJob.maximumPoolSize | default 2 | toString | quote }}
            - name: BackgroundTasks__Hangfire__PrepareSchemaIfNecessary
              value: "true"
            {{- range $name, $value := $migrationEnv }}
            {{- if ne $name "BackgroundTasks__Hangfire__PrepareSchemaIfNecessary" }}
            - name: {{ $name }}
              value: {{ $value | toString | quote }}
            {{- end }}
            {{- end }}
            {{- if not $standalone }}
            {{- with $v.extraEnv }}
            {{- toYaml . | nindent 12 }}
            {{- end }}
            {{- end }}
          resources:
            {{- toYaml $resources | nindent 12 }}
          {{- if $standalone }}
          volumeMounts:
            - name: valkey-trust
              mountPath: /app/valkey-trust
              readOnly: true
            - name: postgresql-trust
              mountPath: /app/postgresql-trust
              readOnly: true
          {{- else }}
          {{- with $v.extraVolumeMounts }}
          volumeMounts:
            {{- toYaml . | nindent 12 }}
          {{- end }}
          {{- end }}
      {{- if $standalone }}
      volumes:
        - name: valkey-trust
          secret:
            secretName: {{ $v.valkeyTrustSecretName }}
            defaultMode: 0440
            items:
              - key: ca.crt
                path: ca.crt
        - name: postgresql-trust
          secret:
            secretName: {{ $v.postgresqlTrustSecretName }}
            defaultMode: 0440
            items:
              - key: ca.crt
                path: ca.crt
      {{- else }}
      {{- with $v.extraVolumes }}
      volumes:
        {{- toYaml . | nindent 8 }}
      {{- end }}
      {{- with $v.nodeSelector }}
      nodeSelector:
        {{- toYaml . | nindent 8 }}
      {{- end }}
      {{- end }}
{{- end -}}
{{- end -}}
