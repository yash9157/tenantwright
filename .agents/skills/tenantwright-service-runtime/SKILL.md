---
name: tenantwright-service-runtime
description: Backend runtime workflow for dependency injection, options, Valkey caching, distributed locks, provider-neutral messaging, tenancy, HTTP clients, health, logging, metrics, and tracing.
---
# Service Runtime

Keep service policy in its owning service and reusable infrastructure in the existing `Shared.*` owner.

## Inspect First

1. Read root and backend governance, then inspect the caller, registration, options, failure behavior, tenant context, and tests.
2. Use [runtime source trace](references/runtime-workflow.md) and [shared ownership](references/shared-module-map.md).
3. For shared changes, apply [shared module rules](references/shared-module-rules.md).
4. Load only the relevant focused reference: [Valkey caching and locks](references/redis-caching-locking.md), [HTTP resilience](references/http-client-resilience.md), [messaging](references/eventbus-messaging.md), [tenancy and current user](references/tenancy-current-user-security.md), [health and telemetry](references/health-observability-runtime.md), or [verification](references/verification-matrix.md).

## Required Workflow

1. Classify the change as service-local runtime policy, reusable infrastructure, or cross-service coordination.
2. Reuse the established `Shared.*` abstraction; do not create a parallel cache, lock, message bus, tenant, current-user, token, audit, or telemetry stack.
3. Bind non-trivial settings to typed options. Validate security-, transport-, and persistence-critical settings at startup.
4. Bound timeouts, retries, concurrency, queues, leases, payloads, retention, and shutdown.
5. Preserve tenant dimensions in caches, locks, messages, database fan-out, logs, and metrics.
6. Test startup failure, cancellation, outage, duplicate delivery, and tenant isolation where applicable.

## Hard Rules

- `Shared.*` is infrastructure-only; service business rules and service contracts stay with the service.
- Missing or invalid security-critical configuration fails closed.
- Never log secrets, tokens, full connection strings, message bodies, or sensitive tenant data.
- `Messaging:Provider` is exactly `None`, `Wolverine`, or `MassTransit`; never silently substitute a provider.
- Route API and `*Eto` shape to `tenantwright-api-event-contracts`, jobs and workflows to `tenantwright-background-processing`, transactions to `tenantwright-unit-of-work`, and schema/query work to `tenantwright-postgresql-efcore`.

## Definition of Done

Ownership is explicit, failure behavior is bounded and observable, tenant isolation is preserved, focused tests pass, and affected flat documentation such as `docs/configuration.md`, `docs/messaging.md`, or `docs/troubleshooting.md` matches the runtime.
