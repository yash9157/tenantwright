# Execution Safety

## Claims and Ownership

Claim work with an atomic transition or the existing runner guarantee. When one owner is required across instances, use the existing distributed lock with a bounded lease. A contender that does not acquire mandatory ownership performs no mutation.

## Bounded Work

Bound selection, argument size, rows per transaction, concurrency, retries, and retention. Advance keyset cursors beyond non-actionable rows so one record cannot block all later work.

## Interruption and Recovery

A claimed or running state needs a defined abandoned-work transition, lease-expiry rule, and authorized bounded recovery path. Restart must not silently lose ownership or repeat non-idempotent effects.

## Cancellation and Failure

Exclude cancellation from generic failure handling, release resources, and rethrow. If a failure transition cannot be persisted, fail the execution visibly rather than returning success with an unresolved claim.

## External Effects

Use stable operation IDs, idempotency keys, unique constraints, receipts, deterministic object names, or checksums. Define recovery when the remote result is unknown. Do not hold a database transaction while performing external I/O, sleeping, or waiting for another runner.
