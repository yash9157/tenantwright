# PostgreSQL Provider Rules

Use Npgsql behavior intentionally; do not assume another relational provider proves it.

## Provider-Specific Features

Evaluate PostgreSQL types such as `jsonb`, arrays, ranges, date/time types, UUIDs, generated columns, and full-text search from actual query and lifecycle needs. Define serialization, indexing, migration, and compatibility behavior before adoption.

Extensions require an owner, supported hosting path, license review, provisioning/migration strategy, upgrade and restore evidence, and behavior when unavailable. Do not enable extensions manually outside managed schema/deployment ownership.

## Concurrency and Locking

- Prefer optimistic concurrency for ordinary aggregate writes.
- Use explicit row/advisory locks only when the invariant cannot be protected more simply.
- Keep lock order deterministic and transactions short.
- Define timeout, cancellation, deadlock retry, idempotency, and observability behavior.
- Test PostgreSQL isolation, savepoints, `FOR UPDATE`, advisory locks, and deadlock behavior against PostgreSQL.

## Date, Time, and Text

Use UTC instants for cross-zone timestamps and explicit local-date/time types for calendar concepts. Review case sensitivity, collation, pattern matching, and normalization before relying on text equality or indexes.

## Discovery

From `backend/`:

```powershell
rg -n "UseNpgsql|Npgsql|HasPostgresExtension|ForUpdate|Advisory|jsonb|tsvector" app services shared tests -g "*.cs"
```

Keep provider concerns in the owning EntityFrameworkCore project unless they are truly shared infrastructure.
