# Multi-tenancy

All tenants share one database per service by default. Rows that a tenant owns implement `IMultiTenant`, and `SharedDbContext` applies a tenant query filter to them automatically. A host operator can also move three services to a dedicated database per tenant: AuditLogs, Notifications and Permissions.

## Tenant resolution

Resolution is implemented in `shared/Shared.MultiTenancy` and `shared/Shared.AspNetCore.Host/Middleware/MultiTenancyMiddleware.cs`. It runs in a fixed order:

1. The `tenantid` claim of an authenticated user (`SharedClaimTypes.TenantId`).
2. The `X-Tenant-Id` header.

| Outcome | Response |
|---|---|
| Ambiguous or invalid input (several values, not a GUID, empty GUID) | `400 invalid_tenant` |
| Claim and header name different tenants | `403 forbidden_tenant` |
| Tenant unknown or inactive | `403 forbidden_tenant` |
| Nothing supplied | Request runs in **host** context (`CurrentTenant.Id == null`) |

The gateway removes `X-Tenant-Id` from client requests, so external callers get their tenant only from the token. Auth sets the claim from the user's tenant. The password grant also checks the requested `tenant_id` against the user's tenant.

Messages carry a signed scope and tenant id, and consumers restore the tenant before running a handler (see [Messaging](messaging.md#trust-model)).

## Database catalog

[`TenantWrightDatabaseCatalog`](../backend/shared/Shared.Data/ConnectionStrings/TenantWrightDatabaseCatalog.cs) names every logical database:

- `AuditLogs`, `Auth`, `Identity`, `Notifications`, `Permissions`, `Tenant`, `Workflows`.

`TenantDedicatable` lists only `AuditLogs`, `Notifications` and `Permissions`. The others are excluded for two reasons: Auth, Identity and Tenant DbContexts are not tenant-filtered, and Workflows owns no tenant data. Each DbContext declares its name, for example:

```csharp
[ConnectionStringName(TenantWrightDatabaseCatalog.AuditLogs)]
public sealed class AuditLogsDbContextApp : SharedDbContext<AuditLogsDbContextApp>
```

## Connection-string resolution

[`TenantConnectionStringResolver`](../backend/shared/Shared.EntityFrameworkCore/ConnectionStrings/TenantConnectionStringResolver.cs) resolves connection strings. In **host context** it tries, in order:

1. `ConnectionStrings:<Name>`
2. A database mapping
3. `ConnectionStrings:Default`

If nothing resolves, it throws while `ThrowOnMissing=true`.

In **tenant context** it applies these rules in order:

1. **Unknown tenant:** throws (fail closed).
2. **Inactive tenant:** throws, regardless of any fallback setting.
3. **Non-dedicatable name:** the host connection string is used. Tenant-stored values are never consulted.
4. **The tenant has a dedicated value for that name:** it is decrypted and used. If its normalized target (see [Target normalization](#dedicated-database-api)) is the service's shared database, resolution fails closed.
5. **No dedicated value and `Shared:Data:ConnectionStringResolver:AllowHostFallbackForTenant=true`:** the shared database is used, and a fallback metric is recorded.
6. **Otherwise:** throws.

There is no tenant-level `Default` fallback.

Every service sets `AllowHostFallbackForTenant` to `true` in `appsettings.json` and in its chart. That is the shared-database default. With `false`, every tenant must have a dedicated database for each dedicatable name.

Resolved tenant records are cached in-process for `TenantCacheSeconds` (30 s). The cache is cleared when the tenant's database assignment changes.

Stored tenant connection strings are encrypted with AES-256-GCM. The ciphertext is bound to the tenant id and the database name (`TenantConnectionStringProtector`). Configure the key under `TenantConnectionStrings:Protection`:

- `CurrentKeyId`
- `CurrentKeyVersion`
- `Keys:<keyId>` (a base64-encoded 32-byte key)
- `RequireProtectedValues`

Old key ids keep decrypting for as long as they remain in `Keys`. The local AppHost generates a key on first run (parameter `tenant-connection-protection-key`, key id `tenant-local-v1`) and gives it to every service, so dedicated databases work locally. The database itself must already exist; create it on the local PostgreSQL container first.

## Dedicated database API

The API is in [`TenantDatabasesController`](../backend/services/Tenant/Tenant/Controllers/Tenants/TenantDatabasesController.cs) and `TenantDatabaseAppService`. Its route is `api/tenant/tenants/{id}/databases`, which the gateway exposes at `/g2/tenants/{id}/databases`. Callers need `Tenant.Management` plus a fine-grained permission.

| Verb | Path | Permission | Purpose |
|---|---|---|---|
| GET | `/` | `Tenant.Management.ConnectionStrings.Read` | List each dedicatable name: dedicated or not, plus a target fingerprint. Connection strings are never returned |
| POST | `/{name}/test` | `Tenant.Management.ConnectionStrings.Test` | Open the stored connection (5 s timeout). Returns only a generic success or failure message |
| PUT | `/{name}` | `Tenant.Management.ConnectionStrings.Update` | Assign or replace. Body: `ConnectionString`, `TestBeforeSave`, `ConcurrencyStamp`, `AcknowledgeNoDataMigration` |
| DELETE | `/{name}?AcknowledgeNoDataMigration=true` | `Tenant.Management.ConnectionStrings.Delete` | Return the tenant to the shared database |

Rules:

- **Host only.** The calls are rejected in tenant context.
- **Allowed names.** `{name}` must be one of the dedicatable catalog names.
- **Acknowledgement.** `AcknowledgeNoDataMigration` must be `true` on every assign and every remove.
- **Duplicate targets.** An assignment is rejected (`DedicatedDatabaseTargetInUse`) when its target matches a shared service database or any other assignment, including another tenant's or another name's. The shared databases are the Tenant service's own connection strings plus `TenantDatabases:SharedTargets:{AuditLogs|Notifications|Permissions}` (see [Shared database targets](#shared-database-targets)).
- **Target normalization.** Targets are compared on a fingerprint of (host, port, database) from [`PostgreSqlDatabaseTarget`](../backend/shared/Shared.EntityFrameworkCore/TenantDatabases/PostgreSqlDatabaseTarget.cs). Credentials and options are ignored. Host names compare case-insensitively without a trailing dot, every loopback spelling (`localhost`, `*.localhost`, `127.0.0.0/8`, `::1`) is one host, an omitted port is 5432, and database names compare case-insensitively. Multi-host lists compare as a set.
- **Best effort for other routes.** No DNS lookup happens, so a second DNS name or a different port to the same server (a proxy and a mapped port, for example) is not detected here. The owning service is the authoritative back-stop: it fails the tenant closed when the target is its own shared database, and its migrator refuses a database that already holds another service's schema (see below).
- **Concurrency.** Optimistic, through `ConcurrencyStamp`.
- **No data movement.** Existing rows stay where they are, and the response always reports `DataMigrated=false`. Copying data is an operator task.
- **No database creation.** The target database must already exist. Creating it, with an owner able to run migrations, is an operator task.

## Migration and change propagation

1. **Publish.** When an assignment is saved or removed, Tenant publishes `TenantDatabaseChangedEto` through the outbox. The event carries `TenantId`, `DatabaseName`, `ChangeKind` (`Assigned`, `Reassigned`, `Updated`, `Removed`) and `TargetFingerprint`.
2. **Migrate.** The owning service (AuditLogs, Notifications or Permissions) handles the event with [`TenantDatabaseMigrator`](../backend/shared/Shared.EntityFrameworkCore/TenantDatabases/TenantDatabaseMigrator.cs). It clears the caches, re-resolves the assignment and checks the fingerprint matches. Then, under a per-tenant distributed lock, it runs EF migrations and seeding in tenant scope.
3. **Transient convergence.** A not-yet-visible assignment (`TenantDatabaseAssignmentNotVisibleException`, expected for up to `TenantCacheSeconds` after Tenant commits) and a migration lock held by another replica (`TenantDatabaseMigrationBusyException`) are marked transient (`ITenantWrightTransientMessageFailure`). Wolverine retries them in process after 1, 5, 15, 30 and 60 s before dead-lettering; MassTransit's immediate retries and 30/120/600 s delayed redelivery already cover them. See [Messaging](messaging.md#failure-handling).
4. **Fail closed until migrated.** Until a tenant's dedicated database is migrated, access to it fails closed (`TenantDatabaseMigrationGuard`). The guard logs a warning at most once per database every 5 minutes; every blocked unit of work is counted (`shared.tenant_databases.not_migrated_total`).
5. **Back-stop.** The guard hands a database it blocks to [`TenantDatabaseMigrationReconciler`](../backend/shared/Shared.EntityFrameworkCore/TenantDatabases/TenantDatabaseMigrationReconciler.cs), so a database whose event was dead-lettered still converges without a restart. It attempts the migration 15 s later, under the same lock, then backs off (30 s doubling to 10 min) while it keeps failing, logging a warning per attempt.
6. **Startup.** With `EfCore:AutoMigrate=true`, each service migrates its shared database and then every dedicated database (`MigrateAllDedicatedAsync`). With `EfCore:MigrateAndExit=true`, any failure fails the run.

How a migration runs:

- **Own connection.** The migration DbContext is created from a fresh service scope with its own connection. It never joins the inbox transaction the handler runs in. EF applies every pending migration and its history rows in one transaction, so a failed or interrupted attempt leaves nothing behind and the retry starts clean. No service migration contains a non-transactional statement.
- **Seeding after migration.** Seeding runs afterwards in its own unit of work. If it fails, the schema stays migrated and the retry only seeds.
- **Ownership check.** Before any DDL, the migrator reads the database catalog and `__EFMigrationsHistory`. A database is refused with `TenantDatabaseForeignSchemaException`, a permanent failure, when it holds migrations of another DbContext, or tables while none of this service's migrations are applied. This happens, for example, when one physical database is given to two services. Assign an empty database; the tenant stays closed until then.

This propagation needs messaging, except for the reconciler. With `Messaging:Provider=None`, dedicated databases are migrated at startup and by the reconciler once tenant work reaches them.

## Shared database targets

The Tenant service has no connection to the other services' shared databases. To reject an assignment that points at one, it reads their targets, never their credentials, from `TenantDatabases:SharedTargets:<Name>:{Host,Port,Database}`. `<Name>` is `AuditLogs`, `Notifications` or `Permissions`, and `Port` defaults to 5432. Any other key, such as `Password` or `ConnectionString`, fails startup. A name without a target is logged as a startup warning.

- **AppHost:** derives each target from the PostgreSQL resource endpoint and the service's database name.
- **Charts:** set `sharedDatabaseTargets` in the `tenantwright-tenant` values (schema-validated), using the host and port those services connect with. See `charts/environments/production/backend/tenant.values.yaml.example`.

Stored `TargetFingerprint` values are derived from the decrypted connection string. At startup, after protection backfill and key rewrap, `TenantConnectionTargetFingerprintRefreshService` recomputes them under a global lock, so a normalization change needs no schema migration. Two assignments that only now resolve to the same target cannot share a fingerprint under the unique index. Both are cleared and logged as an error, and new assignments are compared against them on the decrypted value until an operator re-points one.

## Background work across databases

[`TenantDatabaseFanOut`](../backend/shared/Shared.EntityFrameworkCore/TenantDatabases/TenantDatabaseFanOut.cs) runs a unit of work on the shared database in host scope, then on each dedicated database in that tenant's scope.

- **Dedicated database failure:** logged and counted (`shared.tenant_databases.fan_out_failures_total`), and processing continues.
- **Shared database failure:** rethrown after the dedicated databases have run.

Users include the outbox dispatcher and inbox retention, AuditLogs sealing and queries, Notifications delivery and retention, and the Permissions cache refresh and seeding. Cross-database paged queries are capped at a 10,000-row window.

## Notification dispatch scope

Dispatches, recipients, steps and routing events are stored in the owning
Notifications database and implement `IMultiTenant`. A checked `ScopeId` equals
`TenantId`, with the empty UUID reserved for null host scope. Scope-qualified
foreign keys protect parent/recipient/step linkage, including host rows. The
scope/request unique index prevents nullable host IDs from bypassing deduplication.
A channel notification can link only one step in its own scope.

Coordination uses the existing per-database fan-out and ownership predicates.
Tenant API callers cannot select another tenant. Host lists default to host
intent; an explicit tenant selects its owning database. Host UUID lookups locate
the owning database and reject ambiguous ownership. Current-tenant routes require
an active tenant and never fall back to host data. Quota reservations isolate
scope/channel counts while applying the intentional shared global cap.
