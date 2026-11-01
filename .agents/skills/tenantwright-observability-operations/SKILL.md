---
name: tenantwright-observability-operations
description: Design and operate source-grounded telemetry, alerts, dashboards, and response.
---

# TenantWright Observability Operations

Use this skill when the main outcome is operator detection, diagnosis, alerting, dashboarding, telemetry routing, or observability recovery.

## Inspect First

1. Read [references/observability-map.md](references/observability-map.md) to choose the active telemetry path.
2. Inspect `backend/orchestration/TenantWright.ServiceDefaults/` for application instrumentation and export behavior.
3. Inspect `backend/charts/tenantwright-observability/` or `backend/charts/tenantwright-telemetry/`, including environment overrides and secret references.
4. Use [references/telemetry-standards.md](references/telemetry-standards.md) for signal design.
5. Use [references/alerts-dashboards-runbooks.md](references/alerts-dashboards-runbooks.md) and [references/incident-response-and-rollout.md](references/incident-response-and-rollout.md) for operator-facing changes.
6. Validate with [references/stack-assets-and-validation.md](references/stack-assets-and-validation.md).

## Guardrails

- Do not emit credentials, tokens, tenant-sensitive payloads, or high-cardinality identifiers.
- Keep telemetry failure from changing authorization or tenant-isolation behavior.
- Bound labels, retention, query cost, queues, and exporter retries.
- Reference existing Kubernetes Secrets; never place secret values in chart values or output.
- Treat dashboards as diagnostic aids and alerts as actionable operator contracts.
- Obtain explicit approval before starting the AppHost, containers, cluster operations, or Helm operations.

## Outcome

A complete change identifies the signal producer, collection path, storage/query owner, dashboard or alert consumer, recovery action, verification evidence, and any live check not run.
