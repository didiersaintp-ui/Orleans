{{/*
Expand the name of the chart.
*/}}
{{- define "ticketing.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" }}
{{- end }}

{{/*
Create a default fully qualified app name.
*/}}
{{- define "ticketing.fullname" -}}
{{- if .Values.fullnameOverride }}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" }}
{{- else }}
{{- $name := default .Chart.Name .Values.nameOverride }}
{{- if contains $name .Release.Name }}
{{- .Release.Name | trunc 63 | trimSuffix "-" }}
{{- else }}
{{- printf "%s-%s" .Release.Name $name | trunc 63 | trimSuffix "-" }}
{{- end }}
{{- end }}
{{- end }}

{{/*
Create chart name and version as used by the chart label.
*/}}
{{- define "ticketing.chart" -}}
{{- printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" }}
{{- end }}

{{/*
Common labels
*/}}
{{- define "ticketing.labels" -}}
helm.sh/chart: {{ include "ticketing.chart" . }}
{{ include "ticketing.selectorLabels" . }}
{{- if .Chart.AppVersion }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
{{- end }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end }}

{{/*
Selector labels
*/}}
{{- define "ticketing.selectorLabels" -}}
app.kubernetes.io/name: {{ include "ticketing.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end }}

{{/*
Create the name of the service account to use
*/}}
{{- define "ticketing.serviceAccountName" -}}
{{- if .Values.serviceAccount.create }}
{{- default (include "ticketing.fullname" .) .Values.serviceAccount.name }}
{{- else }}
{{- default "default" .Values.serviceAccount.name }}
{{- end }}
{{- end }}

{{/*
Redis connection string
*/}}
{{- define "ticketing.redisConnection" -}}
{{- if .Values.redis.external.enabled }}
{{- printf "%s:%d" .Values.redis.external.host (.Values.redis.external.port | int) }}
{{- else }}
{{- printf "%s.%s.svc.cluster.local:%d" .Values.redis.host .Release.Namespace (.Values.redis.port | int) }}
{{- end }}
{{- end }}
