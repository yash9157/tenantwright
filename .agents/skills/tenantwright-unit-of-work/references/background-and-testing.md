# Background UoW and Testing

## Background Owners

Hangfire jobs, native workers, Temporal activities, seeders, and other non-HTTP callbacks are not automatically request-owned. Enter an explicit executor scope for each database read or write unit unless source proves interception owns it.

Keep leases, distributed locks, network calls, Valkey operations, blob/filesystem work, compression, checksums, and retry delays outside database transactions. Cancellation must escape the executor and outer processing boundary. Retries must re-enter a fresh scope and rely on idempotent domain transitions, inbox/outbox state, or concurrency checks rather than assuming an interrupted transaction committed.

## Tests

Cover applicable behavior:

- context acquisition occurs after executor entry;
- failed writes roll back with the real UoW/provider composition;
- each batch owns the intended number of transactions;
- child or requires-new completion is independent only when designed that way;
- unique-conflict or savepoint recovery preserves the intended scope;
- cancellation propagates;
- external I/O observes no active database transaction;
- tenant context is restored and cleared correctly;
- inbox/outbox and business state commit or roll back together for message handling.

Use recording fakes only for order assertions. Use SQLite or PostgreSQL for relational transaction evidence; use PostgreSQL for provider-specific isolation, locking, savepoints, and concurrency. EF InMemory cannot prove rollback or relational atomicity.
