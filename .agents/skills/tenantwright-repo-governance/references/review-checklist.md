# Governance Review Checklist

## Scope and Ownership

- Desired outcome and non-goals are explicit.
- Existing owner and affected callers were inspected.
- No duplicate service, shared abstraction, configuration path, or deployment owner was introduced.
- Every changed line belongs to the requested outcome.

## Architecture and Security

- Layering and project-reference direction remain valid.
- Shared libraries remain infrastructure-only.
- Tenant context is required and validated where applicable.
- Authorization is backend-enforced and fails closed.
- Input, secrets, logs, and telemetry are handled safely.
- Retries, concurrency, cancellation, and partial failure are addressed at relevant boundaries.

## Data and Contracts

- Contract and schema changes represent the intended final shape.
- Migration lineage was checked before changing existing migrations.
- Cross-service communication uses owned contracts rather than implementation references.
- Generated or derived artifacts match their sources.

## Delivery

- Tests cover success and important failure paths.
- Configuration, registration, charts, docs, and cleanup are complete.
- Subagents changed only assigned paths and authoritative material stayed with its owner.
- Task records reflect material scope, decisions, status, acceptance, and handoff.

## Signoff

- Fresh focused checks passed.
- Diff and final status were reviewed.
- Skipped checks and remaining risks are explicit.
- Full Quality Gates were run only with the exact required user instruction.
