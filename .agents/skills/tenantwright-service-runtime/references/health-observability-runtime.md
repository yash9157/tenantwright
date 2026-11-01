# Health and Observability Runtime

Use this reference for service-side startup validation, health checks, structured logging, metrics, traces, and HTTP host behavior. Deployment probes and telemetry backends belong to their specialist skills.

## Startup and Host

- Bind non-trivial settings to options and use `ValidateOnStart` for required values.
- Fail startup for unsupported providers or missing mandatory security and persistence settings.
- Keep hosts thin and middleware order intentional.
- Preserve centralized ProblemDetails and environment-safe API description exposure.
- Prevent singleton services from capturing scoped dependencies.

## Health

Liveness reports whether the process can continue; readiness reports whether it can safely receive work. Do not make liveness depend on every remote dependency. Include mandatory dependencies in readiness, bound health timeouts, and avoid exposing internal topology or configuration values.

## Logging, Metrics, and Tracing

- Use structured events and preserve request, message, and job correlation.
- Record the failure stage, retry attempt, and dependency category without payloads or secrets.
- Keep metric dimensions bounded; identifiers do not belong in labels.
- Instrument HTTP, PostgreSQL, Valkey, RabbitMQ, message processing, locks, and workers where useful.
- `TenantWright.ServiceDefaults` owns common OpenTelemetry composition; do not create a second pipeline in a service.
- Preserve cancellation and avoid layered retries that multiply attempts.

Run focused registration and options tests. Running-host evidence is required for middleware, endpoint, readiness, and exporter claims, and starting a host requires explicit user approval. Update `docs/configuration.md` or `docs/troubleshooting.md` when operator behavior changes.
