# Schema and Index Governance

## Keys and Identifiers

Choose identifiers from ownership and contract lifetime, then verify the established pattern in the owning service. Independently addressable or durable contract identities normally need globally safe values; tightly owned internal rows may use database-generated numeric keys when that is the local pattern. Do not introduce a new key strategy without documenting generation, serialization, foreign-key, ordering, and migration consequences.

## Mapping Rules

- Configure lengths, precision, requiredness, enum conversion, timestamps, concurrency tokens, and provider column types deliberately.
- Match CLR nullability, domain validation, and database nullability.
- Use database constraints for invariants that must survive concurrent writers.
- Keep tenant-owned uniqueness tenant-qualified. Handle nullable host scope explicitly rather than assuming nulls compare as equal.
- Specify delete behavior for every ownership-sensitive relationship.
- Keep provider defaults deterministic and safe during inserts, migrations, and tests.

## Index Rules

Add an index only for a demonstrated constraint, join, filter, ordering, or measured plan need. Before adding one, inspect primary keys, unique constraints, foreign-key indexes, column order, selectivity, write cost, and overlapping indexes.

For tenant-owned hot paths, tenant scope generally leads the useful index unless a stronger query pattern proves otherwise. Match filter and ordering columns to the actual SQL. Include columns only when provider behavior and plan evidence justify their maintenance cost.

## Changes Requiring PostgreSQL Evidence

Use PostgreSQL for filtered/partial indexes, expression indexes, generated columns, provider-specific types, collation behavior, concurrency/locking, extension-backed features, and planner claims. SQLite and EF InMemory are not substitutes for these semantics.

## Verification

Inspect the EF model, generated migration, designer, and snapshot. Test duplicate rejection, null behavior, tenant isolation, delete behavior, generation, and concurrency as applicable. For performance indexes, record representative SQL and `EXPLAIN (ANALYZE, BUFFERS)` only against approved non-sensitive data and bounded workloads.
