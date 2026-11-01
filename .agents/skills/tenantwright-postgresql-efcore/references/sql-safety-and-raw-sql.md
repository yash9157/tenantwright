# SQL Safety and Raw SQL

Use clear translatable LINQ for ordinary queries, but choose parameterized SQL or a PostgreSQL routine when it materially improves capability, correctness, maintainability, or measured performance.

## Safety Rules

- Parameterize every value. Never interpolate tenant IDs, identifiers, search text, limits, ordering tokens, or payloads into SQL.
- Whitelist unavoidable identifiers and sort expressions from closed enums or mappings.
- Raw SQL bypasses EF query filters. Enforce tenant and soft-delete boundaries explicitly.
- Keep SQL inside the owning service and DbContext transaction. Do not open an independent connection or transaction inside an active unit of work.
- Preserve concurrency tokens, domain invariants, audit behavior, and durable messaging effects that tracked writes normally provide.
- Bound rows, execution time, retries, and cancellation.
- Never log SQL parameters that may contain secrets or tenant data.

## Direct Writes

Before `ExecuteUpdate`, `ExecuteDelete`, bulk copy, or command SQL, identify which validation, concurrency, events, audit records, and cache invalidation are skipped. Restore required behavior explicitly and test retries and partial failures. A faster direct write is invalid if it weakens those semantics.

## Stored Functions and Procedures

Define ownership, signature, result contract, privileges, tenant enforcement, transaction behavior, migration lifecycle, observability, and test strategy. Prefer invoker rights. If definer rights are unavoidable, fix `search_path`, schema-qualify objects, grant narrowly, and review injection and privilege escalation risks.

Do not commit inside a routine called by an application-owned transaction. Version routine changes through migrations and test both creation and invocation on PostgreSQL.

## Verification

Inspect generated SQL and parameter binding. Test injection-resistant values, wrong-tenant access, empty and boundary inputs, cancellation, rollback, concurrency, and provider behavior. Use a PostgreSQL plan for performance claims and preserve equivalent output/invariants when comparing LINQ and SQL paths.
