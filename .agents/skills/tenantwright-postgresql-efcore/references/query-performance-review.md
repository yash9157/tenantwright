# Query Performance Review

## Default Read Shape

Start with a translatable `Select` containing only fields needed by the response and its authorization, validation, and concurrency decisions. Apply filters and stable ordering before materialization. `AsNoTracking` reduces tracking cost but does not narrow selected columns.

## Tenant Safety

Verify the actual DbContext and enabled global filter before adding or removing an explicit tenant predicate. Do not duplicate a proven current-tenant filter without a path-specific reason. Explicit predicates remain appropriate for raw SQL, authorized fan-out, filter-bypass paths, tenant-qualified joins, and defense where the current tenant is not the query owner.

Dedicated tenant databases are opt-in only for AuditLogs, Notifications, and Permissions. Cross-tenant work for those services must use `TenantDatabaseFanOut` or the established topology rather than assuming one database contains every tenant.

## Review Steps

1. Identify caller, expected cardinality, bounds, authorization, and required consistency.
2. Inspect LINQ and generated SQL; confirm filters, joins, projection, ordering, and paging.
3. Eliminate query-per-row loops and avoidable sequential foreign-key lookups.
4. Preserve missing-record semantics, locks, concurrency revalidation, and external-decision boundaries when consolidating reads.
5. Use keyset pagination for large ordered sets when offset cost or drift matters; always provide deterministic tie-breakers.
6. Bound collections and fan-out. Never materialize an unbounded tenant or host-wide result.
7. Check index support against the final SQL.
8. Use a PostgreSQL plan when translation, selectivity, index use, or planner behavior is material.

## Plan Safety

Run plans only on approved environments and non-sensitive parameters. `EXPLAIN ANALYZE` executes the statement; do not use it for writes or expensive production reads without explicit authorization. Record row estimates versus actual rows, scans, joins, sorts, buffers, and execution time without exposing tenant data.

## Tests

Cover result correctness, authorization/tenant isolation, empty and boundary pages, deterministic ordering, cancellation, and command count when round trips are the claim. Provider-specific SQL and planner claims require PostgreSQL evidence.
