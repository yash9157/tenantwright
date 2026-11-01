# Operations and Testing

## Focused Tests

Select applicable cases:

- duplicate execution produces one logical effect;
- lock contention performs no work;
- lease loss or cancellation stops and propagates;
- abandoned claims recover under a new owner;
- retry resumes safely after an ambiguous external effect;
- batches are bounded and independently transactional;
- invalid rows do not block later eligible work;
- external I/O occurs outside database transactions;
- required processing fails readiness when disabled or invalid;
- DI resolves every job, activity, handler, and worker dependency.

Use real composition when behavior depends on UoW, serialization, locking, or runner registration. Recording fakes are suitable only for narrow call-order checks.

## Operational Contract

Document options, queue or task-queue names, cron expressions, concurrency, locks, retry and recovery policy, health checks, metrics, and non-sensitive log events. Distinguish registration tests from real PostgreSQL, Valkey, Hangfire, Quartz, Temporal, or broker execution. Starting any host or infrastructure requires user approval.
