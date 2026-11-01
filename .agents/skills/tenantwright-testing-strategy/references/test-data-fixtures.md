# Test Data and Fixtures

## Principles

- Build only state relevant to the behavior under test.
- Prefer named factory methods or builders that express domain intent.
- Use deterministic IDs and clocks when identity or time matters.
- Make tenant ownership explicit in every multi-tenant fixture.
- Keep security defaults denied; individual tests opt into grants or claims.
- Do not embed credentials, tokens, connection strings, or generated secrets.

## Shared Fixtures

Use collection fixtures for expensive resources and per-test scopes for mutable state. A fixture owns startup, readiness, reset, and disposal of every resource it creates. Do not depend on execution order or state left by another test.

When parallel tests share infrastructure, isolate by database/schema, tenant, queue name, cache prefix, or another production-equivalent namespace. Random suffixes may isolate names, but keep assertions deterministic and report seeds for property-based failures.

## Time and Concurrency

Inject `TimeProvider` where supported. Avoid `Task.Delay` as synchronization; wait on observable state with a bounded timeout. For concurrency tests, coordinate actors with barriers or explicit signals and assert the final durable invariant.

## Mocks

Moq is centrally available but optional. Mock external or expensive boundaries, not the domain behavior under test. Configure strict expectations only when interaction order is itself the contract; otherwise assert the observable result and important durable side effects.
