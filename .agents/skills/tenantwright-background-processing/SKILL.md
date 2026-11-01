---
name: tenantwright-background-processing
description: Backend workflow for Hangfire jobs, Quartz tasks, Temporal workflows and activities, hosted workers, bounded execution, ownership, cancellation, recovery, and idempotency.
---
# Background Processing

Design background work for retry, interruption, concurrency, and operator diagnosis.

## Inspect First

1. Read root and backend governance, then trace registration, enqueue or start, serialization, execution, persistence, recovery, and health.
2. Select the current owner with [runner selection](references/runner-selection.md).
3. Apply [execution safety](references/execution-safety.md) and [operations and testing](references/operations-and-testing.md).
4. For Workflows or Temporal callers, also read [Temporal workflows](references/temporal-workflows.md).

## Required Workflow

1. Use the existing runner that owns the behavior; do not introduce another queue or scheduler for convenience.
2. Define durable identity, tenant scope, bounded arguments, retry policy, idempotency, cancellation, and recovery before coding.
3. Use distributed ownership when concurrent execution could corrupt state or duplicate effects. Failure to acquire mandatory ownership fails closed.
4. Split database work into bounded UoW scopes and keep network, blob, filesystem, serialization, compression, and delays outside transactions.
5. Emit structured, non-sensitive progress and failure signals. Required workers must affect readiness.

## Hard Rules

- Never swallow `OperationCanceledException`; release owned resources and rethrow.
- Never report success while a durable claim remains unresolved or failure-state persistence failed.
- Bound polling, concurrency, arguments, retries, transactions, batches, and retention.
- In-memory locks do not provide multi-instance ownership.
- Do not enqueue secrets, tokens, or sensitive tenant payloads.
- Make duplicate execution safe with stable identity, constraints, receipts, or explicit claim transitions.

## Definition of Done

Runner ownership, tenant scope, cancellation, retry, recovery, and process-loss behavior are explicit; focused failure and contention tests pass; configuration and health are aligned; and affected flat documentation such as `docs/configuration.md` or `docs/troubleshooting.md` is current.
