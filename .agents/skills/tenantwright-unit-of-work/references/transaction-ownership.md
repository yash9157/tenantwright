# Transaction Ownership

## HTTP Requests

The shared MVC and Razor filters own automatic request scopes. Safe HTTP methods default non-transactional; mutating and unknown methods default transactional. Method metadata may deliberately override the default. Disabled metadata suppresses automatic activation but does not authorize unowned database access.

Trace the complete request through result execution and post-processing. A failed action must not become committable because an exception was handled later. Rollback or disposal errors must not hide the original failure.

Do not add UoW attributes to every query merely to activate a DbContext. Use metadata only for a deliberate policy exception such as non-transactional execution, disabled ownership, isolation, timeout, or requires-new behavior.

## Intercepted Application Paths

Prefer an established intercepted boundary over a nested manual scope. Confirm the call crosses the interceptor; self-invocation, delegates, and manually constructed instances may not.

## Executor Paths

Use `IUnitOfWorkExecutor` for non-intercepted work:

- transactional execution owns one bounded atomic database transition;
- non-transactional execution provides ambient context for bounded reads or orchestration without a database transaction.

Resolve repositories or `IDbContextProvider<TDbContext>` normally, but acquire the context inside the executor delegate. Repositories in the same scope must resolve the same context.

Use repositories for aggregate-oriented persistence when their contract fits. Use the DbContext provider for owned multi-entity workflows, projections, joins, or set-based work that repositories would obscure. Neither choice moves domain invariants into persistence code.

## Direct Manager Use

Direct `IUnitOfWorkManager.Begin` is exceptional. Use it only when an intentional child, requires-new, or independently completed scope needs options unavailable through the executor. Make completion ownership obvious and test independent commit and rollback.

## Messages and Durable Persistence

The provider-neutral messaging pipeline restores tenant context and invokes `ITenantWrightMessageHandler<T>` inside the consuming UoW. Business changes and neutral inbox/outbox persistence must share the intended atomic boundary. Transport selection (`Wolverine`, `MassTransit`, or `None`) must not change domain transaction semantics. Do not start a competing UoW around a handler already owned by the consumer pipeline.

## Batches and External Work

Use one fresh transactional scope per atomic batch. Select work without a long transaction when safe, then re-read or revalidate concurrency-sensitive state inside the write transaction. Keep calls to remote services, Valkey, blob storage, filesystem operations, delays, and expensive serialization outside the database transaction unless atomicity demands and the design explicitly accepts the cost.
