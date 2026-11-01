# Telemetry Standards

## Logs

Use structured events with stable names and severity. Include service, environment, trace correlation, and bounded operational dimensions. Exclude credentials, token material, tenant-owned content, and raw request or response bodies.

## Metrics

Use low-cardinality dimensions. Prefer rates, durations, queue depth, retry counts, saturation, and outcome counters. Define units and the operator question each metric answers. Do not use unbounded IDs as labels.

## Traces

Create spans at inbound requests, outbound calls, persistence, messaging, and background boundaries where they explain latency or failure. Record safe outcome metadata, not payloads. Keep health endpoints excluded from routine tracing as configured in ServiceDefaults.

## Health

Liveness answers whether the process can run. Readiness answers whether it can safely receive traffic. Dependency degradation belongs in diagnostics and alerts unless it truly makes the service unable to serve safely.

## Export

Use OTLP through configured endpoints. Bound batches, memory, retries, and queues. Telemetry export failure must not broaden access, cross tenant boundaries, or crash a healthy request path solely to preserve observability.
