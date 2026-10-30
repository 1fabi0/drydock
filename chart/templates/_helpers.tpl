{{/*
Expand the name of the chart.
*/}}
{{- define "drydock.name" -}}
{{- include "common.names.name" . -}}
{{- end -}}

{{/*
Create a default fully qualified app name.
*/}}
{{- define "drydock.fullname" -}}
{{- include "common.names.fullname" . -}}
{{- end -}}

{{/*
Allow the release namespace to be overridden.
*/}}
{{- define "drydock.namespace" -}}
{{- include "common.names.namespace" . -}}
{{- end -}}

{{/*
Standard labels.
*/}}
{{- define "drydock.labels" -}}
{{- include "common.labels.standard" (dict "customLabels" .Values.commonLabels "context" $) -}}
{{- end -}}

{{/*
Match labels for selectors.
*/}}
{{- define "drydock.matchLabels" -}}
{{- include "common.labels.matchLabels" (dict "customLabels" .Values.podLabels "context" $) -}}
{{- end -}}

{{/*
Match labels as a comma-separated selector string for API queries: key1=val1,key2=val2
*/}}
{{- define "drydock.matchLabelsSelector" -}}
{{- $labelsYaml := include "drydock.matchLabels" . -}}
{{- $labelsDict := fromYaml $labelsYaml -}}
{{- $pairs := list -}}
{{- range $k, $v := $labelsDict -}}
  {{- $pairs = append $pairs (printf "%s=%s" $k ($v | toString)) -}}
{{- end -}}
{{- join "," $pairs -}}
{{- end -}}

{{/*
Get the S3 secret name.
*/}}
{{- define "drydock.backupSecretName" -}}
{{- if .Values.backup.existingSecret -}}
{{- .Values.backup.existingSecret -}}
{{- else -}}
{{- printf "%s-s3-credentials" (include "drydock.fullname" .) -}}
{{- end -}}
{{- end -}}

{{/*
Get the TLS secret name.
*/}}
{{- define "drydock.tlsSecretName" -}}
{{- if .Values.tls.certManager.secretName -}}
{{- .Values.tls.certManager.secretName -}}
{{- else -}}
{{- printf "%s-tls" (include "drydock.fullname" .) -}}
{{- end -}}
{{- end -}}

{{/*
Get the Issuer name.
*/}}
{{- define "drydock.issuerName" -}}
{{- printf "%s-ca-issuer" (include "drydock.fullname" .) -}}
{{- end -}}

{{/*
Return the CSI driver name or fail with a clear message if invalid for metadata.name.
*/}}
{{- define "drydock.driverName" -}}
{{- $rawName := required "A valid .Values.plugin.name is required!" .Values.plugin.name -}}
{{- if contains "/" $rawName -}}
  {{- fail (printf "Invalid plugin.name '%s': CSI driver names cannot contain '/' because Kubernetes uses it as metadata.name for the CSIDriver resource. Use dots or hyphens instead (e.g., '1fabi0.drydock.io' or 'csi.drydock.io')." $rawName) -}}
{{- end -}}
{{- if not (regexMatch "^[a-z0-9]([-a-z0-9.]*[a-z0-9])?$" $rawName) -}}
  {{- fail (printf "Invalid plugin.name '%s': Must be a valid RFC 1123 DNS subdomain (lowercase alphanumeric, '.' or '-', starting and ending with alphanumeric)." $rawName) -}}
{{- end -}}
{{- $rawName -}}
{{- end -}}