# Configuration

Services use standard ASP.NET Core configuration: `appsettings.json`, then environment variables, then user secrets in Development. In environment variables, nested keys use a double underscore (`ConnectionStrings__Default`). The checked-in `appsettings.json` files never contain secrets. Where a secret is needed they leave an empty value for the AppHost, user secrets or Kubernetes Secrets to fill in.

Many settings are checked at startup. An invalid or missing security-critical value stops the host instead of falling back to a default. Examples are a non-HTTPS `AuthServer:Authority` outside Development, a missing `ConnectionStrings:Default`, and invalid messaging trust settings.

## Key sections

| Section | Read by | Notes |
|---|---|---|
| `ConnectionStrings:Default` | every service | Host (shared) PostgreSQL database. Required outside Development |
| `ConnectionStrings:<CatalogName>` | `TenantConnectionStringResolver` | Optional per-catalog override in host context. The catalog names are `AuditLogs`, `Auth`, `Identity`, `Notifications`, `Permissions`, `Tenant` and `Workflows` ([`TenantWrightDatabaseCatalog`](../backend/shared/Shared.Data/ConnectionStrings/TenantWrightDatabaseCatalog.cs)). If no override is set, resolution falls back to `Default` |
| `Shared:Data:ConnectionStringResolver` | Shared.Data / EF Core | `ThrowOnMissing` (default `true`; must stay `true` outside Development), `AllowHostFallbackForTenant` (code default `false`; every service's `appsettings.json` and chart sets it to `true`), `TenantCacheSeconds` (30). See [Multi-tenancy](multi-tenancy.md) |
| `TenantConnectionStrings:Protection` | Tenant and dedicatable services | AES-256-GCM protection of stored tenant connection strings: `CurrentKeyId`, `CurrentKeyVersion`, `RequireProtectedValues`, `Keys:<keyId>` (base64 32-byte key). The local AppHost generates the key (`tenant-local-v1`) and gives it to every service |
| `Caching:Valkey` | every service | `Configuration` (StackExchange.Redis syntax, which must not include a password), `Password`, `User`, `TrustedCaCertificatePath` (requires `ssl=true`), `InstanceName`. `Caching:Provider` is `Valkey` |
| `AuthServer` | services, gateway | `Authority` (issuer URL; must be HTTPS outside Development), `Audiences` |
| `OpenIddict` | Auth | `Issuer`, `Clients:{Internal,TenantWrightGateway,NotificationsInternal}`, `SigningCertificate`/`EncryptionCertificate` (`Path`, `Password`), `AllowPasswordFlow` (Development only) |
| `RemoteFeatureChecker` | services | Client-credentials call to Permissions' feature check: `BaseAddress`, `ClientId` (`auth-internal`), `ClientSecret`, `Scope` (`features.check`), `FailOpen` (`false`) |
| `IdentityPreferencesClient`, `IdentityAudienceClient` | Notifications | Delivery-time Identity lookups: `BaseAddress`, `ClientProfile` (`NotificationsInternal`, the `OpenIddict:Clients:NotificationsInternal` credential Notifications holds), `Scope` (`identity-internal`), `TimeoutSeconds`. Validated at startup. There is no implicit default profile: a profile the host lacks fails closed |
| `TenantImpactEnrichment` | Tenant | Optional impact-preview counts from Identity and Notifications: `IdentityBaseAddress`, `NotificationsBaseAddress`, `ClientProfile` (`Internal`, the `OpenIddict:Clients:Internal` credential), `TimeoutMilliseconds` |
| `TenantDatabases:SharedTargets` | Tenant | `<AuditLogs\|Notifications\|Permissions>:{Host,Port,Database}`: the dedicatable services' shared databases, as targets only, so dedicated assignments to them are rejected. Credential keys fail startup. See [Multi-tenancy](multi-tenancy.md#shared-database-targets) |
| `Messaging` | every service | `Provider` (`None` \| `Wolverine` \| `MassTransit`; `None` in `appsettings.json`), `ServiceName`, `ProducerPrincipal`, `RabbitMq:*`, `Trust:*`, `Delivery:*`, `Replay:*`, `Outbox:*`, `Inbox:*`. See [Messaging](messaging.md) |
| `AuditLogging` | every service (capture); AuditLogs (sealing) | Capture options, `Signatures` (`CurrentKeyId`, `Keys:<keyId>`, `RequireSignatures`, `FreshnessTolerance`), plus AuditLogs-only `Sealing`, `Archive`, `Security`, `OperationalHealth` |
| `Temporal` | Workflows | `Enabled`, `WorkerEnabled`, `TargetHost`, `Namespace`, task queues, TLS (`UseTls`, `TlsDomain`, `ServerRootCaCertificatePath`, `ClientCertificatePath`, `ClientPrivateKeyPath`), `ApiKeyPath` |
| `EfCore:AutoMigrate` | every service | `true` applies pending migrations on startup, to the shared database and then every dedicated tenant database. It is `false` in `appsettings.json`; the AppHost sets it to `true` |
| `EfCore:MigrateAndExit` | every service | With `AutoMigrate`: migrate, fail if any tenant database failed, then exit before serving traffic. The Helm migration Job uses this mode |
| `SessionValidation` | services | Server-side session marker checks (`Enabled`, `Strict`) |
| `GatewayLimits`, `ReverseProxy`, `Cors`, `RateLimiting`, `ForwardedHeaders` | gateway | Limits are validated at startup |
| `Hosting:HttpsRedirection:Enabled` | every host | Default `true`. Set it to `false` on services reached over plain HTTP behind a TLS-terminating boundary; the charts and the AppHost do this for the backend services. A redirect on an internal call makes `HttpClient` drop the `Authorization` header, so the call fails with 401 |

Options classes worth reading:

- `MessagingOptions`/`MessagingOptionsValidator` (`shared/Shared.EventBus/Messaging/`)
- `SharedConnectionStringResolverOptions` and `TenantConnectionStringProtectionOptions` (`shared/Shared.Data/ConnectionStrings/`)
- `ValkeyConnectionConfiguration` (`shared/Shared.Caching/`)
- `AuditLogSignatureOptions` (`shared/Shared.AuditLogs/Publishing/`)
- `TemporalWorkflowOptions` (`services/Workflows/Workflows.Application/Temporal/`)

## What the AppHost injects

For each service, [`TenantWrightServiceExtensions.cs`](../backend/orchestration/TenantWright.AppHost/TenantWrightServiceExtensions.cs) sets:

- `ASPNETCORE_ENVIRONMENT=Development`
- `ConnectionStrings__Default` (the service's own database)
- `EfCore__AutoMigrate=true`
- `Caching__Valkey__Configuration` and `Caching__Valkey__Password`

[`LocalMessaging.cs`](../backend/orchestration/TenantWright.AppHost/LocalMessaging.cs) adds the `Messaging__*` settings: the provider, principal, broker credentials and trust keys. `AppHost.cs` adds the generated client secrets, seed users, the audit seal key, and the Temporal target for Workflows.

## Running one service outside the AppHost

Use this to debug a single host from your IDE. The rest of the system can keep running under the AppHost.

1. **Stop the AppHost copy.** Pin the infrastructure ports you need (see [Getting started](getting-started.md#what-the-apphost-starts)), start the AppHost, then use the dashboard's **Stop** action on the service you want to run yourself. Its ports are now free.
2. **Copy the connection details.** Take the database connection string and the Valkey endpoint and password from the dashboard resource details.
3. **Supply settings.** At minimum, set:
   - `ConnectionStrings:Default`
   - `Caching:Valkey:Configuration` and `Caching:Valkey:Password`
   - `RemoteFeatureChecker:ClientSecret` (the AppHost's `internal-client-secret` parameter)
   - `EfCore:AutoMigrate=true` if the database is new

   `Messaging:Provider` defaults to `None` from `appsettings.json`, so the service runs bus-free. Its published events are dropped, and commands such as audit writes fail closed. To join the local broker, copy the `Messaging__*` variables from the dashboard's environment view of the stopped resource.

   Auth, Identity, Notifications and the gateway have a `UserSecretsId`, so you can use user secrets:

   ```powershell
   dotnet user-secrets set "ConnectionStrings:Default" "<connection string>" --project services/Identity/Identity
   dotnet user-secrets set "Caching:Valkey:Password" "<valkey password>" --project services/Identity/Identity
   ```

   The other hosts have no `UserSecretsId`, so use environment variables for the session:

   ```powershell
   $env:ConnectionStrings__Default = '<connection string>'
   $env:Caching__Valkey__Configuration = 'localhost:<port>'
   $env:Caching__Valkey__Password = '<valkey password>'
   $env:RemoteFeatureChecker__ClientSecret = '<internal-client-secret>'
   ```

4. **Run it.** `dotnet run --project services/AuditLogs/AuditLogs`. The launch profile sets `ASPNETCORE_ENVIRONMENT=Development` and the same ports as the AppHost.

`AuthServer:Authority` defaults to `https://localhost:7239/`, the Auth HTTPS endpoint, so token validation works against an AppHost-hosted Auth.

## Design-time EF Core factories

`dotnet ef` uses a design-time factory in each service. It reads an environment variable for the connection string:

- `AUDITLOGS_DB_CONNECTION`
- `IDENTITY_DB_CONNECTION`
- `NOTIFICATIONS_DB_CONNECTION`
- `PERMISSIONS_DB_CONNECTION`
- `WORKFLOWS_DB_CONNECTION`

Tenant has its own `TenantDesignTimeDbContextFactory` in the host project. Without the variable, the factories fall back to a local `localhost` connection string. Restore the pinned tool with `dotnet tool restore`.
