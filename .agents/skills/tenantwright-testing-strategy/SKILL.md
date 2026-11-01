---
name: tenantwright-testing-strategy
description: Backend test selection, fixtures, relational and container coverage, assertions, and verification.
---
# TenantWright Testing Strategy

Use the smallest test surface that proves changed behavior without hiding authorization, tenant, provider, transaction, concurrency, messaging, or runtime risk.

## Read First

Start with `references/test-project-structure.md` and `references/test-selection.md`. Load only the detail needed:

- `references/test-data-fixtures.md` for fixtures and builders.
- `references/database-and-container-tests.md` for EF, PostgreSQL, Valkey, RabbitMQ, and container evidence.
- `references/quality-tooling-and-http-tests.md` for architecture checks and HTTP pipelines.
- `references/naming-and-assertions.md` for readable test shape.
- `references/coverage-and-verification.md` for focused completion evidence.

## Workflow

1. State the observable behavior and failure mode.
2. Add a regression that fails for the expected reason before the fix when practical.
3. Test the owning boundary first; expand only across real contracts or runtime boundaries.
4. Use real domain/application/shared infrastructure where practical and mock only external or expensive boundaries.
5. Cover authorization, tenant isolation, validation, cancellation, retry, idempotency, concurrency, transactions, and fail-closed configuration when they are on the path.
6. Use SQLite or PostgreSQL when EF InMemory cannot prove relational behavior.
7. Update `docs/testing.md` when test ownership or repository-wide guidance changes.

## Hard Rules

- No unbounded waits, real sleeps, shared mutable state, or unowned random ports.
- Do not assert only mock calls when observable behavior is available.
- Do not claim PostgreSQL, Valkey, RabbitMQ, distributed-lock, or multi-process behavior from a weaker fake.
- Run `scripts/run-quality-gates.ps1` only when the user says "Run Quality Gates".

## Definition of Done

Tests prove relevant success and failure behavior at the owning boundary, crossed boundaries have adjacent evidence, tests are deterministic and tenant-safe, and skipped live/container checks are explicit.
