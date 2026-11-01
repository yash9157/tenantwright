# Incident Response and Rollout

## Response

1. Confirm the symptom from more than one signal when possible.
2. Establish affected services, environments, tenants, and time range without querying sensitive payloads.
3. Check recent configuration, image, chart, dependency, and schema changes.
4. Mitigate with the smallest reversible action that preserves authorization and tenant isolation.
5. Verify recovery from service-level indicators and direct health evidence.
6. Record the timeline, cause, evidence, and follow-up owner.

## Rollout

For telemetry changes, validate rendering and configuration first, then deploy gradually where the platform permits. Watch collector rejection, queue pressure, cardinality, storage growth, query latency, and alert noise. Define rollback before changing exporters, retention, or routing. Live cluster checks and Helm operations require explicit approval.
