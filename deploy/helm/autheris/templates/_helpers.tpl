{{/*
Expand the name of the chart.
*/}}
{{- define "autheris.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" }}
{{- end }}

{{/*
Create a default fully qualified app name.
*/}}
{{- define "autheris.fullname" -}}
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
{{- define "autheris.chart" -}}
{{- printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" }}
{{- end }}

{{/*
Common labels
*/}}
{{- define "autheris.labels" -}}
helm.sh/chart: {{ include "autheris.chart" . }}
{{ include "autheris.selectorLabels" . }}
{{- if .Chart.AppVersion }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
{{- end }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end }}

{{/*
Labels of the governance migration job (ConfigMap, Job, pod). Deliberately NOT derived from the application selector labels:
the name differs, so the Service, ServiceMonitor, PodDisruptionBudget and the application NetworkPolicy never select the job pod.
*/}}
{{- define "autheris.migrationLabels" -}}
helm.sh/chart: {{ include "autheris.chart" . }}
app.kubernetes.io/name: {{ printf "%s-migrate" (include "autheris.name" .) | trunc 63 | trimSuffix "-" }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- if .Chart.AppVersion }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
{{- end }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
app.kubernetes.io/component: governance-migration
{{- end }}

{{/*
Selector labels
*/}}
{{- define "autheris.selectorLabels" -}}
app.kubernetes.io/name: {{ include "autheris.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end }}

{{/*
Create the name of the service account to use
*/}}
{{- define "autheris.serviceAccountName" -}}
{{- if .Values.serviceAccount.create }}
{{- default (include "autheris.fullname" .) .Values.serviceAccount.name }}
{{- else }}
{{- default "default" .Values.serviceAccount.name }}
{{- end }}
{{- end }}

{{/*
Container image reference. Renders repo@digest when image.digest is set (immutable),
otherwise repo:tag where tag defaults to the chart appVersion (never a floating "latest").
Fails when image.requireDigest is true and no digest is configured.
*/}}
{{- define "autheris.image" -}}
{{- if .Values.image.digest -}}
{{- printf "%s@%s" .Values.image.repository .Values.image.digest -}}
{{- else -}}
{{- if .Values.image.requireDigest -}}
{{- fail "image.digest is required (image.requireDigest=true): set --set image.digest=sha256:<digest> to pin the image immutably" -}}
{{- end -}}
{{- printf "%s:%s" .Values.image.repository (default .Chart.AppVersion .Values.image.tag) -}}
{{- end -}}
{{- end }}

{{/*
Gateway configuration shared by the application ConfigMap and the governance migration hook ConfigMap.
Rendered as the entries of a ConfigMap "data" block.
*/}}
{{- define "autheris.configData" -}}
Gateway__GovernanceDb__Provider: {{ .Values.gateway.governanceDb.provider | quote }}
Gateway__DrainDelaySeconds: {{ .Values.gateway.drainDelaySeconds | quote }}
Gateway__ShutdownTimeoutSeconds: {{ .Values.gateway.shutdownTimeoutSeconds | quote }}
Gateway__Authentication__ForwardAuth__RequireTrustedProxy: "true"
{{- $allowedHosts := .Values.gateway.allowedHosts }}
{{- if not $allowedHosts }}
{{- $allowedHosts = list }}
{{- range .Values.ingress.hosts }}
{{- $allowedHosts = append $allowedHosts .host }}
{{- end }}
{{- end }}
{{- if not $allowedHosts }}
{{- fail "gateway.allowedHosts (or ingress.hosts) must list the host names clients use; the gateway refuses to start without Gateway:Hosting:AllowedHosts." }}
{{- end }}
{{- range $i, $h := $allowedHosts }}
Gateway__Hosting__AllowedHosts__{{ $i }}: {{ $h | quote }}
{{- end }}
{{- end }}

{{/*
Gateway environment variables shared by the application container and the governance migration job container.
Rendered as the entries of a container "env" list.
*/}}
{{- define "autheris.gatewayEnv" -}}
- name: ASPNETCORE_ENVIRONMENT
  value: {{ .Values.gateway.aspnetcoreEnvironment | quote }}
- name: ASPNETCORE_URLS
  value: "http://+:{{ .Values.service.targetPort }}"
- name: Gateway__DrainDelaySeconds
  value: {{ .Values.gateway.drainDelaySeconds | quote }}
- name: Gateway__ShutdownTimeoutSeconds
  value: {{ .Values.gateway.shutdownTimeoutSeconds | quote }}
{{- if .Values.gateway.governanceDb.existingSecret }}
- name: Gateway__GovernanceDb__ConnectionString
  valueFrom:
    secretKeyRef:
      name: {{ .Values.gateway.governanceDb.existingSecret }}
      key: {{ .Values.gateway.governanceDb.connectionStringSecretKey }}
{{- end }}
{{- if and .Values.gateway.redis.enabled .Values.gateway.redis.existingSecret }}
- name: Gateway__Caching__Redis__Configuration
  valueFrom:
    secretKeyRef:
      name: {{ .Values.gateway.redis.existingSecret }}
      key: {{ .Values.gateway.redis.configurationSecretKey }}
- name: Gateway__Caching__Redis__InstanceName
  value: {{ .Values.gateway.redis.instanceName | quote }}
{{- if hasKey .Values.gateway.redis "failClosedOnRevocationLookupError" }}
- name: Gateway__Caching__Redis__FailClosedOnRevocationLookupError
  value: {{ .Values.gateway.redis.failClosedOnRevocationLookupError | quote }}
{{- end }}
- name: Gateway__Caching__Redis__RevocationLookupGraceSeconds
  value: {{ .Values.gateway.redis.revocationLookupGraceSeconds | default 60 | quote }}
{{- end }}
{{- end }}
