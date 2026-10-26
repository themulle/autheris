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
{{- /* SEC2-09: authentication, trusted proxy and tenant isolation settings. */}}
{{- $enforce := .Values.gateway.production.enforceRequiredSettings }}
{{- $auth := .Values.gateway.authentication }}
{{- $enabled := list }}
{{- range $name := list "entraId" "adfs" "forwardAuth" "kerberos" }}
{{- if (index $auth $name).enabled }}
{{- $enabled = append $enabled $name }}
{{- end }}
{{- end }}
{{- if and $enforce (ne (len $enabled) 1) }}
{{- fail (printf "gateway.authentication: exactly one of entraId, adfs, forwardAuth, kerberos must be enabled when gateway.production.enforceRequiredSettings is true (enabled: %d)" (len $enabled)) }}
{{- end }}
{{- if $auth.entraId.enabled }}
Gateway__Authentication__EntraId__Enabled: "true"
Gateway__Authentication__EntraId__TenantId: {{ required "gateway.authentication.entraId.tenantId is required when entraId is enabled" $auth.entraId.tenantId | quote }}
Gateway__Authentication__EntraId__ClientId: {{ required "gateway.authentication.entraId.clientId is required when entraId is enabled" $auth.entraId.clientId | quote }}
Gateway__Authentication__EntraId__Audience: {{ required "gateway.authentication.entraId.audience is required when entraId is enabled" $auth.entraId.audience | quote }}
{{- end }}
{{- if $auth.adfs.enabled }}
Gateway__Authentication__Adfs__Enabled: "true"
Gateway__Authentication__Adfs__Authority: {{ required "gateway.authentication.adfs.authority is required when adfs is enabled" $auth.adfs.authority | quote }}
Gateway__Authentication__Adfs__Audience: {{ required "gateway.authentication.adfs.audience is required when adfs is enabled" $auth.adfs.audience | quote }}
{{- end }}
{{- if $auth.kerberos.enabled }}
Gateway__Authentication__EnableKerberos: "true"
{{- end }}
{{- if $auth.forwardAuth.enabled }}
Gateway__Authentication__ForwardAuth__Enabled: "true"
{{- if not $auth.forwardAuth.trustedProxies }}
{{- fail "gateway.authentication.forwardAuth.trustedProxies must list the proxy addresses when forwardAuth is enabled" }}
{{- end }}
{{- range $i, $p := $auth.forwardAuth.trustedProxies }}
Gateway__Authentication__ForwardAuth__TrustedProxies__{{ $i }}: {{ $p | quote }}
{{- end }}
{{- $fa := $auth.forwardAuth }}
{{- $networks := $fa.trustedNetworks }}
{{- if not $networks }}
{{- if $enforce }}
{{- fail "gateway.authentication.forwardAuth.trustedNetworks must be set explicitly when forwardAuth is enabled and gateway.production.enforceRequiredSettings is true (the defaults in gateway.security.trustedNetworks are not accepted)" }}
{{- end }}
{{- $networks = .Values.gateway.security.trustedNetworks }}
{{- end }}
{{- range $i, $n := $networks }}
Gateway__Authentication__ForwardAuth__TrustedNetworks__{{ $i }}: {{ $n | quote }}
{{- end }}
{{- if and $enforce (not $fa.sharedSecret.existingSecret) (not $fa.sharedSecret.keyVaultRef) }}
{{- fail "gateway.authentication.forwardAuth.sharedSecret.existingSecret (or sharedSecret.keyVaultRef) is required when forwardAuth is enabled and gateway.production.enforceRequiredSettings is true" }}
{{- end }}
{{- with $fa.sharedSecret.keyVaultRef }}
Gateway__Authentication__ForwardAuth__SharedSecretKeyVaultRef: {{ . | quote }}
{{- end }}
{{- end }}
{{- $ds := .Values.gateway.dataSources }}
{{- if and $enforce (not $ds.requireTenantColumn) (not $ds.requireTenantColumnExemption) }}
{{- fail "gateway.dataSources.requireTenantColumn must be true when gateway.production.enforceRequiredSettings is true (or set gateway.dataSources.requireTenantColumnExemption=true to accept shared, tenant-less tables explicitly)" }}
{{- end }}
Gateway__DataSources__RequireTenantColumn: {{ $ds.requireTenantColumn | ternary "\"true\"" "\"false\"" }}
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
# The root filesystem is read-only; async job results go to the writable /tmp emptyDir.
- name: Gateway__AsyncJobs__ScratchDirectory
  value: "/tmp/autheris-jobs"
{{- if and .Values.gateway.authentication.forwardAuth.enabled .Values.gateway.authentication.forwardAuth.sharedSecret.existingSecret }}
- name: Gateway__Authentication__ForwardAuth__SharedSecret
  valueFrom:
    secretKeyRef:
      name: {{ .Values.gateway.authentication.forwardAuth.sharedSecret.existingSecret }}
      key: {{ .Values.gateway.authentication.forwardAuth.sharedSecret.key | default "sharedSecret" }}
{{- end }}
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
{{/*
Admin API exposure (SEC-281-6): "true" only when ingress.exposeAdmin is the boolean true; any other value (string, null, number) keeps the deny rule.
*/}}
{{- define "autheris.exposeAdmin" -}}
{{- if and (kindIs "bool" .Values.ingress.exposeAdmin) .Values.ingress.exposeAdmin -}}true{{- end -}}
{{- end }}

{{/*
WAVE3-STARTUP-SC-03: one HA timing value. Only a positive whole number is accepted (a number from values or --set); unset, a string or
out of the application range (HighAvailabilityOptions [Range]) fails the render, so a rendered config never fails app startup and there is no silent
default that could pass the NF-HA-01 checks. Usage: include "autheris.haSeconds" (dict "key" "gateway.x" "value" .Values.gateway.x "min" 1 "max" 30)
*/}}
{{- define "autheris.haSeconds" -}}
{{- if not (or (kindIs "float64" .value) (kindIs "int64" .value) (kindIs "int" .value)) -}}
{{- fail (printf "%s must be a positive whole number of seconds (NF-HA-01)" .key) -}}
{{- end -}}
{{- $f := float64 .value -}}
{{- if or (le $f 0.0) (ne (floor $f) $f) -}}
{{- fail (printf "%s must be a positive whole number of seconds (NF-HA-01)" .key) -}}
{{- end -}}
{{- if or (lt $f (float64 .min)) (gt $f (float64 .max)) -}}
{{- fail (printf "%s must be between %d and %d seconds (NF-HA-01)" .key (int64 .min) (int64 .max)) -}}
{{- end -}}
{{- int64 .value -}}
{{- end }}

{{/*
NF-HA-01 wiring (ARCH-MNT-06-FU-04): renders the four Gateway__HighAvailability__* env vars from values only and fails the render when
(a) shutdown < query + 10 (01a) or (b) grace < 2 * drain + shutdown + 10. The grace budget is stricter than the application check
(drain + shutdown + 10) because the kubelet grace period also covers the preStop sleep, which uses the same drain value.
*/}}
{{- define "autheris.highAvailabilityEnv" -}}
{{- $drain := int64 (include "autheris.haSeconds" (dict "key" "gateway.drainDelaySeconds" "value" .Values.gateway.drainDelaySeconds "min" 1 "max" 30)) -}}
{{- $shutdown := int64 (include "autheris.haSeconds" (dict "key" "gateway.shutdownTimeoutSeconds" "value" .Values.gateway.shutdownTimeoutSeconds "min" 10 "max" 180)) -}}
{{- $query := int64 (include "autheris.haSeconds" (dict "key" "gateway.queryTimeoutSeconds" "value" .Values.gateway.queryTimeoutSeconds "min" 5 "max" 120)) -}}
{{- $grace := int64 (include "autheris.haSeconds" (dict "key" "terminationGracePeriodSeconds" "value" .Values.terminationGracePeriodSeconds "min" 20 "max" 300)) -}}
{{- if lt $shutdown (add $query 10) -}}
{{- fail (printf "NF-HA-01a: gateway.shutdownTimeoutSeconds (%d) must be at least gateway.queryTimeoutSeconds (%d) + 10" $shutdown $query) -}}
{{- end -}}
{{- if lt $grace (add (mul 2 $drain) $shutdown 10) -}}
{{- fail (printf "NF-HA-01b: terminationGracePeriodSeconds (%d) must be at least 2 * gateway.drainDelaySeconds (%d) + gateway.shutdownTimeoutSeconds (%d) + 10" $grace $drain $shutdown) -}}
{{- end -}}
- name: Gateway__HighAvailability__DrainDelaySeconds
  value: {{ $drain | quote }}
- name: Gateway__HighAvailability__ShutdownTimeoutSeconds
  value: {{ $shutdown | quote }}
- name: Gateway__HighAvailability__QueryTimeoutSeconds
  value: {{ $query | quote }}
- name: Gateway__HighAvailability__TerminationGracePeriodSeconds
  value: {{ $grace | quote }}
{{- end }}
