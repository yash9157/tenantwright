{{/*
  The messaging provider a service workload runs, from .Values.messaging.provider:

    None         bus-free: no broker, no messaging trust material, no licence.
    MassTransit  RabbitMQ through MassTransit 9; additionally mounts the MT_LICENSE secret.
    Wolverine    RabbitMQ through Wolverine.

  It mirrors the application's strict Messaging:Provider reader: an absent value is None, and anything that is not
  exactly one provider name fails the render instead of selecting a provider by accident. The application remains
  the authority on which providers are installed in an image and fails its startup for any other.
*/}}
{{- define "tenantwrightcommon.messagingProvider" -}}
{{- $provider := .Values.messaging.provider | default "None" | toString -}}
{{- if not (has $provider (list "None" "MassTransit" "Wolverine")) -}}
{{- fail (printf "messaging.provider must be one of None, MassTransit or Wolverine; got %q" $provider) -}}
{{- end -}}
{{- $provider -}}
{{- end -}}
