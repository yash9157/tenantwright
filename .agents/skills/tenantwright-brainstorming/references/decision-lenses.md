# Decision Lenses

Use only the lenses that can change the recommendation.

## Ownership

- Which service owns the state and invariants?
- Is the behavior service-local, shared infrastructure, or deployment policy?
- Would a new abstraction duplicate an existing owner?

## Tenancy and Authorization

- Where is tenant context established and validated?
- Which backend permission or policy guards every entry point?
- Does failure deny access without falling back to broader scope?

## Contracts and Data

- Is the boundary an API contract, event contract, persistence model, or internal implementation detail?
- Can retries, concurrency, or partial failure create duplicate or contradictory state?
- Does the design require schema migration, data movement, or a new cross-service dependency?

## Operations

- How is failure detected, diagnosed, retried, and recovered?
- What bounds latency, work size, retention, and resource consumption?
- Which configuration, deployment, alert, or runbook owner changes?

## Delivery

- What is the complete final shape?
- Which callers, tests, docs, generated artifacts, and cleanup must change atomically?
- What fresh evidence proves the result?

## Neutral Examples

- Choose between synchronous validation and an asynchronous approval process for tenant onboarding.
- Decide whether notification preferences belong to the account owner or notification service.
- Decide whether a cross-service operation needs an event, an internal HTTP contract, or no new integration.
- Compare shared-database and dedicated-database operation for a tenant-owned service without weakening isolation.
