# Observability Map

## Application Instrumentation

`backend/orchestration/TenantWright.ServiceDefaults/Extensions.cs` configures OpenTelemetry logging, metrics, and tracing. It adds ASP.NET Core, HTTP client, runtime, EF Core, and Valkey-compatible Redis instrumentation; service and shared-infrastructure meters; trace/span activity fields; and `/health` plus `/alive` endpoints. OTLP export is enabled only when the SDK is not disabled and `OTEL_EXPORTER_OTLP_ENDPOINT` is set.

## Main OSS Stack

`backend/charts/tenantwright-observability/` packages:

- Grafana for dashboards and query access;
- Prometheus and Alertmanager for metrics and alerts;
- Tempo for traces;
- Loki and Promtail for logs;
- an OpenTelemetry Collector for OTLP traces, metrics, and logs;
- black-box probes for synthetic health checks;
- hardening, recording and alert rules, dashboards, and backup policy resources.

The collector routes traces to Tempo, exposes metrics for Prometheus, and sends logs to Loki.

## Standalone External Search Path

`backend/charts/tenantwright-telemetry/` is a separate path for an external OpenSearch deployment. It includes an OpenTelemetry Collector, Data Prepper, Fluent Bit, and Prometheus. Credentials come from an existing Kubernetes Secret. Do not assume this chart and the main OSS stack are deployed together; verify the selected environment and values.

## Selection Rule

Start from the environment's installed chart and configured OTLP endpoint. Source defines capability; deployed releases and values define the active path.
