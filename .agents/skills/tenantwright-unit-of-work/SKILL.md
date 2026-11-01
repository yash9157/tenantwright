---
name: tenantwright-unit-of-work
description: Repository workflow for unit-of-work boundaries, DbContext ownership, transactions, rollback, nested scopes, and database-backed workers.
---

# Unit of Work

Make transaction ownership explicit and acquire EF contexts only inside an active repository unit of work.

## Inspect First

1. Read workspace/backend governance and the owning entry point.
2. Read [references/source-map-and-verification.md](references/source-map-and-verification.md) and [references/transaction-ownership.md](references/transaction-ownership.md).
3. For jobs, workers, or workflow activities, also read [references/background-and-testing.md](references/background-and-testing.md) and use `tenantwright-background-processing`.
4. Use `tenantwright-postgresql-efcore` when provider, schema, query, isolation, locking, or savepoint semantics are material.

## Choose the Owner

- MVC actions and Razor Page handlers use the shared request filters. Safe methods (`GET`, `HEAD`, `OPTIONS`, `TRACE`) default non-transactional; mutating and unknown methods default transactional.
- Eligible application-service methods use interception. Verify that the call actually crosses the interceptor.
- Non-intercepted work uses `IUnitOfWorkExecutor.ExecuteTransactionalAsync` for one atomic state transition or `ExecuteNonTransactionalAsync` for a bounded read/orchestration scope.
- Use `IUnitOfWorkManager.Begin` directly only for an intentional child, requires-new, or independently completed scope not expressible by the executor.
- Message handlers implement `ITenantWrightMessageHandler<T>` and run after trust verification and tenant restoration inside the consuming unit of work. Do not create a competing transaction owner.

## Hard Rules

- Call `IDbContextProvider<TDbContext>.GetDbContextAsync()` only after entering the owning scope.
- Do not inject or retain a concrete DbContext across UoW boundaries.
- Do not hold database transactions across network, blob, filesystem, serialization, compression, delays, or other long-running work.
- Do not assume filters cover minimal APIs, SignalR, jobs, workers, Temporal activities, health checks, or seeders. Give every database-backed non-HTTP path an explicit owner.
- Do not stream a response while a request transaction remains open.
- Preserve tenant/data filters, concurrency, audit behavior, and durable outbox/inbox work inside the owning scope.
- Never swallow cancellation or replace the original failure with rollback/disposal errors.
- Use fresh transactional scopes for bounded batches; revalidate concurrency-sensitive state inside each write transaction.
- No ABP packages.

## Verification

Select the applicable matrix in `references/source-map-and-verification.md`. Recording fakes prove call order only; rollback, context reuse, nested transactions, savepoints, and relational concurrency require the real UoW/DI/DbContext composition. Use SQLite where its semantics suffice and PostgreSQL/Testcontainers for provider-specific behavior. Report the entry point, owner, transaction count, context acquisition timing, tenant scope, commit/rollback and cancellation evidence, and skipped checks.
