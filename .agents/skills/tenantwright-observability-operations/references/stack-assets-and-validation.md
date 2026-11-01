# Stack Assets and Validation

## Static Checks

- Build affected hosts when application instrumentation changes.
- Render and lint the affected chart with the intended environment values and secret names only.
- Run `backend/scripts/verify-observability-platform.ps1` for observability chart policy.
- Inspect collector pipelines end to end: receiver, processor, exporter, destination, and query path.
- Check dashboards and alert rules for valid labels, bounded queries, ownership, and safe annotations.

## Main Stack

Confirm OTLP traces reach Tempo, metrics are available to Prometheus, logs reach Loki, Grafana data sources match service names, Alertmanager routing references existing Secrets, and black-box targets are environment-correct.

## External Search Stack

Confirm the selected environment intentionally uses `tenantwright-telemetry`, the external OpenSearch endpoint is supplied through reviewed values, credentials are referenced through the configured Secret, and Collector/Data Prepper/Fluent Bit routes agree.

## Live Checks

Starting local orchestration, installing charts, or querying a live environment requires explicit approval. Report skipped live checks and the resulting residual risk.
