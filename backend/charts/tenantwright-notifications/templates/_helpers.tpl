{{- define "tenantwrightapp.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "tenantwrightapp.fullname" -}}
{{- if .Values.fullnameOverride -}}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- $name := include "tenantwrightapp.name" . -}}
{{- if contains $name .Release.Name -}}
{{- .Release.Name | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- printf "%s-%s" .Release.Name $name | trunc 63 | trimSuffix "-" -}}
{{- end -}}
{{- end -}}
{{- end -}}

{{- define "tenantwrightapp.chart" -}}
{{- printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "tenantwrightapp.labels" -}}
helm.sh/chart: {{ include "tenantwrightapp.chart" . }}
app.kubernetes.io/name: {{ include "tenantwrightapp.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end -}}

{{- define "tenantwrightapp.selectorLabels" -}}
app.kubernetes.io/name: {{ include "tenantwrightapp.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end -}}

{{- define "tenantwrightapp.serviceAccountName" -}}
{{- if .Values.serviceAccount.create -}}
{{- default (include "tenantwrightapp.fullname" .) .Values.serviceAccount.name -}}
{{- else -}}
{{- default "default" .Values.serviceAccount.name -}}
{{- end -}}
{{- end -}}

{{- define "tenantwrightapp.configMapName" -}}
{{- printf "%s-config" (include "tenantwrightapp.fullname" .) | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "tenantwrightapp.secretEnvName" -}}
{{- printf "%s-secret-env" (include "tenantwrightapp.fullname" .) | trunc 63 | trimSuffix "-" -}}
{{- end -}}