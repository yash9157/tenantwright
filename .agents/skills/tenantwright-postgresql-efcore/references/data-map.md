# EF Core Source Map

## Current Owners

```text
backend/app/Auth/Auth/                              OpenIddict host and Auth DbContext
backend/services/<Service>/<Service>.Domain/       entities and domain invariants
backend/services/<Service>/<Service>.EntityFrameworkCore/
  <Service>DbContext.cs                              DbSets and filters
  EntityFrameworkCore/                               model configuration and registration
  Migrations/                                        generated migrations and snapshot
backend/shared/Shared.EntityFrameworkCore/          shared EF, messaging persistence, UoW integration
backend/shared/Shared.EntityFrameworkCore/TenantDatabases/
backend/shared/Shared.Data/ConnectionStrings/TenantWrightDatabaseCatalog.cs
backend/tests/<Service>.Tests/
backend/tests/Shared.EntityFrameworkCore.Tests/
backend/tools/deployment/verify_migration_lineage.py
backend/scripts/reset-local-databases.ps1
```

Current six-project service families are AuditLogs, Identity, Notifications, Permissions, and Tenant. Workflows has host, Application, Application.Contracts, and EntityFrameworkCore projects. Auth, Identity, and Tenant DbContexts are host-only; Workflows owns no tenant data. AuditLogs, Notifications, and Permissions alone support opt-in dedicated tenant databases.

## Trace Before Editing

From `backend/`, discover rather than assume names:

```powershell
rg -n "class .*DbContext|DbSet<|OnModelCreating|HasQueryFilter|UseNpgsql" app services shared tests -g "*.cs"
rg -n "IDbContextProvider|GetDbContextAsync|GetQueryableAsync|FromSql|Execute.*Sql" app services shared tests -g "*.cs"
rg --files app services | rg "Migrations|ModelSnapshot|DbContext.*Factory|EntityFrameworkCore.csproj"
```

For tenant database behavior, inspect `TenantWrightDatabaseCatalog`, `TenantConnectionStringResolver`, `TenantDatabaseFanOut`, `TenantDatabaseMigrator`, `TenantDatabaseMigrationGuard`, `TenantDatabaseSchemaInspector`, `TenantDatabaseMigrationReconciler`, and `PostgreSqlDatabaseTarget` (target fingerprint normalization) plus their tests.

## Questions to Answer

1. Which DbContext and database own the row?
2. Is the context host-only, shared-tenant, or eligible for a dedicated tenant database?
3. Which filter stamps or restricts `TenantId`, and can this path disable it?
4. Which domain invariant and database constraint enforce the same rule?
5. Which queries and contracts depend on the shape?
6. Which migration IDs exist in source, persistent targets, and releases?
7. Which provider can prove the required behavior?
