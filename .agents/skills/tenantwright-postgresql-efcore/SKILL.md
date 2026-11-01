---
name: tenantwright-postgresql-efcore
description: Repository workflow for PostgreSQL and EF Core models, migrations, queries, tenant filters, provider behavior, and relational verification.
---

# PostgreSQL and EF Core

Use this skill when data shape, migration lineage, query correctness, tenant isolation, concurrency, PostgreSQL semantics, or database performance is the main risk. Use `tenantwright-unit-of-work` for transaction ownership and DbContext lifetime.

## Read the Relevant Reference

Always start with [references/data-map.md](references/data-map.md), then read only what the change needs:

- migrations: [references/efcore-migration-commands.md](references/efcore-migration-commands.md)
- relationships: [references/relationships-and-navigation.md](references/relationships-and-navigation.md)
- keys, mappings, constraints, and indexes: [references/schema-and-index-governance.md](references/schema-and-index-governance.md)
- LINQ, projections, paging, and plans: [references/query-performance-review.md](references/query-performance-review.md)
- Npgsql types, extensions, and locking: [references/postgresql-provider-rules.md](references/postgresql-provider-rules.md)
- raw SQL and stored routines: [references/sql-safety-and-raw-sql.md](references/sql-safety-and-raw-sql.md)
- rollout and evidence: [references/schema-rollout-and-verification.md](references/schema-rollout-and-verification.md)

## Repository Workflow

1. Read workspace and backend governance, the owning service, its callers, and relevant task decisions.
2. Trace entity -> mapping extension -> DbContext -> registration -> migration/snapshot -> application query or write -> tests.
3. Confirm the owning database and tenancy mode. Shared databases are the default. Only AuditLogs, Notifications, and Permissions may use dedicated tenant databases, as defined by `TenantWrightDatabaseCatalog.TenantDedicatable`.
4. Establish current model/query behavior before editing. Inspect generated SQL or a PostgreSQL plan when query shape or performance is the risk.
5. Align domain and database invariants. Tenant-qualified uniqueness, concurrency, idempotency, and ownership rules that must survive races belong in database constraints where practical.
6. Generate migrations with the repository toolchain; never invent migration timestamps or hand-edit snapshots.
7. Before changing lineage, compare source migration IDs with `__EFMigrationsHistory` for every persistent target in scope and determine whether artifacts were released. Never print connection strings or credentials.
8. Review generated migration, designer, snapshot, provider types, defaults, delete behavior, filters, messaging tables, and indexes.
9. Run focused model/query/migration tests with a persistence provider capable of proving the behavior.
10. Update the applicable flat canonical documentation under `docs/` when behavior changes.

## Hard Rules

- Applied or released migrations are immutable. Append a corrective migration instead of editing, deleting, renaming, reordering, or regenerating them.
- A persistent database reset requires explicit approval and exact environment, database, DbContext, source-lineage, and applied-ledger verification. Ephemeral test databases created and destroyed by one test run are disposable.
- Global tenant and soft-delete filters are security boundaries. Verify the actual DbContext, filter state, current-tenant scope, and bypass path before changing predicates.
- Raw SQL does not inherit EF query filters. Every tenant-owned command must carry an explicit, parameterized tenant boundary unless an authorized and audited cross-tenant path owns the operation.
- `IDbContextProvider<TDbContext>` requires an active unit of work. Acquire a context inside the intercepted or executor-owned scope; do not retain it across scopes.
- Prefer translatable projections that select only required fields. Full aggregate loading needs a domain reason.
- Review N+1 loops and avoidable fixed lookup chains. Preserve locks, missing-record behavior, and revalidation when consolidating reads.
- Choose indexes from query and constraint evidence. Account for existing primary-key, unique, and foreign-key coverage.
- EF InMemory cannot prove relational constraints, rollback, SQL translation, locking, or PostgreSQL behavior.
- No ABP packages.

## Verification

Use focused tests first, then build affected projects. Use SQLite for relational behavior it supports and PostgreSQL/Testcontainers for Npgsql SQL, extensions, locking, filtered indexes, planner behavior, migration application, and provider-specific concurrency. Report the DbContext, migration command, migration/snapshot review, provider used by each test, query-plan evidence when relevant, documentation status, and checks not run.
