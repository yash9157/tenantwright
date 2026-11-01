# Database and Container Tests

## Choose the Right Persistence Double

- EF InMemory is suitable for simple application orchestration where relational semantics are irrelevant.
- SQLite proves many relational mappings, constraints, transactions, and query translations cheaply.
- PostgreSQL Testcontainers is required for Npgsql-specific types, SQL, indexes, locking, concurrency, migrations, or query behavior.

Never claim provider correctness from EF InMemory. Shared and service test projects already use EF InMemory and/or SQLite; `Shared.EntityFrameworkCore.Tests` includes SQLite and Testcontainers coverage.

## Container Ownership

Use existing fixtures before adding another. The fixture must pin the image through repository configuration, wait for readiness, provide bounded startup, isolate mutable state, and dispose resources. Do not start containers outside tests without explicit user approval.

Current container-oriented ownership includes:

- `Shared.EntityFrameworkCore.Tests` for PostgreSQL/EF behavior;
- `Shared.EventBus.Wolverine.Tests` for transport behavior;
- `Shared.Valkey.IntegrationTests` for Valkey behavior.

## Database Assertions

Test schema constraints and indexes through observable writes or provider metadata as appropriate. Test rollback, savepoints, retries, cancellation, and concurrent updates when they are part of the changed path. Migrations must be exercised against a fresh ephemeral database when migration behavior changes.

Multi-tenant tests must prove both intended access and cross-tenant denial. For dedicated-database paths, verify catalog resolution, migration/fan-out ownership, and failure isolation without weakening the shared-database default.
