# Tenancy, Current User, and Runtime Security

Inspect `Shared.MultiTenancy`, `Shared.Security`, `Shared.CurrentUser`, `Shared.Permissions*`, the enforcing middleware or handler, and the consuming service.

## Tenant Rules

- Keep tenant contributor order explicit and reject malformed, conflicting, or unauthorized input.
- Never fall back to host scope when explicit tenant context exists.
- Restore tenant context only from authenticated request data or verified message attestations.
- Bound and dispose temporary tenant scope changes.
- Keep cache keys, locks, messages, database resolution, and logs tenant-safe.
- Do not implement service-local tenant parsing.

The default is one service database shared by tenants. Dedicated tenant databases are opt-in only for names in `TenantWrightDatabaseCatalog.TenantDedicatable`: AuditLogs, Notifications, and Permissions. Resolution uses `TenantConnectionStringResolver`; migration and cross-database work use `TenantDatabaseTopology`, `TenantDatabaseFanOut`, `TenantDatabaseMigrator`, `TenantDatabaseMigrationGuard`, and the back-stop `TenantDatabaseMigrationReconciler`. The migrator refuses a database that already holds another DbContext's schema (`TenantDatabaseForeignSchemaException`) before any DDL. Those three services handle `TenantDatabaseChangedEto` and migrate through their owning handler. Unknown database names remain host-only.

## Principal and Permission Rules

Use shared claim parsing, `ICurrentPrincipalAccessor`, and `ICurrentUser`; missing identity data never creates a fallback identity. Keep permission semantics in the permissions boundary and generic mechanics in shared code. Authorization cache failures must not become allow decisions.

Never log tokens, claims collections, connection strings, or tenant-owned payloads. Run focused shared tests plus owning-service tests for positive and negative tenant paths, dedicated-database selection, cache isolation, and fail-closed behavior. Update `docs/multi-tenancy.md` or `docs/security.md` when behavior changes.
