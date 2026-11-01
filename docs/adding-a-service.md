# Adding a service

Copy the shape of an existing service. **AuditLogs** is the most complete six-project reference. **Workflows** is the smallest, with no tenant data. Below, `Orders` stands in for your service, `orders` for its lower-case name, and `tenantwright-orders` for its messaging principal. Paths are relative to `backend/`.

Expect to touch many files. Guard tests in `tests/TenantWright.AppHost.Tests`, `tests/Shared.EventBus.Tests` and `tests/Auth.Tests` catch most omissions.

## 1. Projects

Create `services/Orders/` with six projects, mirroring the references of `services/AuditLogs/*/*.csproj`:

| Project | References |
|---|---|
| `Orders.Domain.Shared` | `Shared.Permissions.Abstractions` |
| `Orders.Domain` | `Orders.Domain.Shared`, `Shared.Domain`, `Shared.Domain.Shared`, `Shared.Modularity` |
| `Orders.Application.Contracts` | `Shared.Modularity` |
| `Orders.EntityFrameworkCore` | `Orders.Domain`, `Shared.EntityFrameworkCore`, `Npgsql.EntityFrameworkCore.PostgreSQL` |
| `Orders.Application` | Domain, Contracts and EntityFrameworkCore projects; the `Shared.*` libraries you use (`Shared.EventBus`, `Shared.EventContracts`, `Shared.MultiTenancy`, `Shared.Uow` …) |
| `Orders` (host, `Microsoft.NET.Sdk.Web`) | Application, EntityFrameworkCore, `Shared.AspNetCore.Host`, `Shared.AuditLogs`, `Shared.EventBus.Hosting`, `Shared.Data`, `Shared.Permissions`, `orchestration/TenantWright.ServiceDefaults`; packages `OpenIddict.Validation.AspNetCore`, `OpenIddict.Validation.SystemNetHttp`, `Microsoft.EntityFrameworkCore.Design` |

Rules for package and project references:

- Package versions are central (`Directory.Packages.props`), so `PackageReference` elements carry no `Version`.
- Never reference another service's implementation projects. Only `*.Domain.Shared` or `Shared.EventContracts` may cross a service boundary.

Add a `<Folder Name="/services/Orders/">` with the six projects to `TenantWright.slnx`.

## 2. Modules and host

- **Modules.** Each project has a module deriving from `BaseModule`, with `[DependsOn(...)]`: `OrdersDomainModule`, `OrdersApplicationContractsModule`, `OrdersApplicationModule`, `OrdersEntityFrameworkCoreModule` and `OrdersHostModule`. Copy the `DependsOn` set from `AuditLogsHostModule`, dropping what you don't need (Hangfire, for example).
- **`Program.cs`.** Copy it from `services/Workflows/Workflows/Program.cs` and change the module type.
- **`OrdersHostModule.ConfigureServices`:**
  - Call `services.AddTenantWrightMessagingHost<OrdersDbContextApp>(configuration, TenantWrightMessagingTopology.OrdersPrincipal);`.
  - Use OpenIddict validation with `options.AddAudiences("orders")`.
  - Use `StartupConfigExceptionFactory.InvalidConfiguration(...)` checks for `AuthServer:Authority` (HTTPS) and `ConnectionStrings:Default` outside Development.
- **`OnApplicationInitializationAsync`.** Keep the middleware order: routing, unit of work, audit capture, authentication, session validation, multi-tenancy, authorization, controllers.
- **`appsettings.json`.** Copy it from AuditLogs and keep secrets empty. Set:
  - `AuthServer:Audiences` = `orders`
  - `Messaging:ServiceName` and `Messaging:ProducerPrincipal` = `tenantwright-orders`
  - `Messaging:Provider` = `None`
  - `AuditLogging:ApplicationName`
- **`Properties/launchSettings.json`.** Set the same ports you give the AppHost.
- **`Dockerfile`.** Copy it from `services/AuditLogs/AuditLogs/Dockerfile`. The build context is the `backend/` root.

## 3. Persistence

1. **Catalog name.** Add `public const string Orders = "Orders";` to `shared/Shared.Data/ConnectionStrings/TenantWrightDatabaseCatalog.cs`, and include it in `KnownNames` and `All`. Add it to `TenantDedicatable` only if every tenant-owned row implements `IMultiTenant` and the service should support per-tenant databases. The handler and fan-out wiring that requires is described in [Multi-tenancy](multi-tenancy.md).
2. **DbContext.** Create it as follows:

   ```csharp
   [ConnectionStringName(TenantWrightDatabaseCatalog.Orders)]
   public sealed class OrdersDbContextApp : SharedDbContext<OrdersDbContextApp>
   {
       protected override void OnModelCreating(ModelBuilder modelBuilder)
       {
           base.OnModelCreating(modelBuilder);
           modelBuilder.ConfigureOrders();
           modelBuilder.AddTenantWrightMessagingEntities(); // outbox, inbox, replay tables
       }
   }
   ```

3. **EntityFrameworkCore module.** Copy `AuditLogsEntityFrameworkCoreModule`. It registers `AddDbContext` with Npgsql and `AddSharedDbContextOptionsExtension()`, `AddSharedEfCoreAutoMigration<T>()`, the default repositories and `AddTenantWrightProtectedReplayIdempotencyStore<T>()`.
4. **Design-time factory.** Copy `AuditLogsDbContextAppFactory` and read `ORDERS_DB_CONNECTION`.
5. **Initial migration:**

   ```powershell
   dotnet tool restore
   $env:ORDERS_DB_CONNECTION = '<local connection string>'
   dotnet ef migrations add Initial --project services/Orders/Orders.EntityFrameworkCore --startup-project services/Orders/Orders.EntityFrameworkCore --output-dir Migrations
   ```

## 4. Errors and permissions

- **Error codes.** Put them in `Orders.Domain.Shared` in an `OrdersErrorCodes` static class of snake_case constants, following `AuditLogsErrorCodes`.
- **Permission definitions.** Create `Orders.Domain.Shared/Permissions/OrdersPermissions.cs` and `OrdersPermissionDefinitionProvider.cs`, using the namespace `Shared.Permissions.Definitions.Orders` and following the AuditLogs files.
- **Catalog aggregation.** The Permissions service aggregates definitions from a fixed assembly list:
  - Add a `ProjectReference` from `services/Permissions/Permissions.Application` to `Orders.Domain.Shared`.
  - Add the provider type to `GetCatalogAssemblies()` in `PermissionsApplicationModule.cs`.

## 5. Auth and gateway

- **Auth scope.** In `app/Auth/Auth/Data/OpenIddictDataSeedContributor.cs`:
  - add an `OrdersScopeResources` array (`["OrdersService", "orders"]`);
  - add a `Seed...ApiScopeAsync` call that upserts scope `orders`;
  - add `Prefixes.Scope + "orders"` to the `tenantwright-gateway` client permissions.
- **Other Auth lists.** Add `orders` to the scope allow-list in `app/Auth/Auth/Managers/OpenIddictManagementValidator.cs`, and to `CommonTokenScopeSets` in `app/Auth/Auth/Caching/OpenIddictCacheSeedService.cs` if you want it pre-warmed. Then update `tests/Auth.Tests`.
- **Gateway** (`gateway/ApiGateway/appsettings.json`):
  - add `orders` to `AuthServer:Audiences` (the code fallback in `Program.cs` lists the same names);
  - add a route with the next free prefix (for example `g7`), `PathRemovePrefix` `/g7` and `PathPrefix` `/api/orders`;
  - add an `orders` cluster with destination `orders_1`.
- **Controller routes** should start with `api/orders`. Put internal M2M endpoints under `api/orders/internal/*`; the gateway refuses to expose those.

## 6. Messaging

- **Topology.** In `shared/Shared.EventBus/Messaging/TenantWrightMessagingTopology.cs`:
  - add `public const string OrdersPrincipal = "tenantwright-orders";` and include it in `ServicePrincipals` (this gives it audit-write rights and a permission-cache endpoint);
  - add `Routes` and `Endpoint(...)` entries for its contracts.

  See [Messaging](messaging.md#adding-a-message-contract-and-handler).
- **Local broker.** Add `"tenantwright-orders"` to `LocalMessaging.Principals` in `orchestration/TenantWright.AppHost/LocalMessaging.cs`. The local broker user, permissions and signing key are then generated automatically.
- **Cluster broker.** Add the principal to `charts/tenantwright-rabbitmq/values.yaml` (`principals` and `clientCertificates.principals`). Add a `tenantwright-orders-v1` public key entry to `charts/tenantwright-common/files/messaging.values.yaml`.

## 7. AppHost

In `orchestration/TenantWright.AppHost/AppHost.cs`:

```csharp
var orders = builder.AddTenantWrightService("orders", @"..\..\services\Orders\Orders\Orders.csproj", httpPort: 5167, httpsPort: 7167)
    .WithServiceInfrastructure(infrastructure, "tenantwright_orders")
    .WithEnvironment("RemoteFeatureChecker__ClientSecret", internalClientSecret)
    .WithMessaging(infrastructure, "tenantwright-orders");
```

On the gateway resource, add `.WithReference(orders)` and `.WithGatewayDestination("orders", orders)`. Add a `ProjectReference` to `TenantWright.AppHost.csproj`, and update the service lists asserted in `tests/TenantWright.AppHost.Tests`.

## 8. Charts

1. **Chart.** Copy `charts/tenantwright-auditlogs` to `charts/tenantwright-orders`. Rename it in `Chart.yaml`, and update `values.yaml`:
   - `OTEL_SERVICE_NAME`, `AuthServer__Audiences`
   - the messaging secret names
   - `secretRefs`, such as `tenantwright-orders-db`
2. **Installer.** Add an `$orderedCharts` entry (Workload, Messaging) to `charts/helm-install-all.ps1`.
3. **Secrets script.** In `charts/apply-secrets.ps1`, add the service to `$databaseServices` and `$messagingServices`, the verification-key list, and `Restart-AppDeployments`.
4. **Environment files.** Add `backend/orders.values.yaml` to `charts/environments/dev` and `backend/orders.values.yaml.example` to `charts/environments/production`. Add image digest entries to `images.lock.yaml` and its `.example`, and a services entry to `postgresql-pools.yaml.example`.
5. **Gateway values.** Add the audience and cluster address to `charts/tenantwright-gateway/values.yaml`.
6. **Verify.** Run `scripts/verify-messaging-charts.ps1` and `scripts/verify-helm-environment-layout.ps1`; both carry their own service lists.

## 9. Tests and verification

- **Tests.** Add `tests/Orders.Tests/Orders.Tests.csproj` (xUnit; see `tests/AuditLogs.Tests`) and list it under the `/tests/` folder in `TenantWright.slnx`.
- **Checks.**
  - `dotnet build TenantWright.slnx`
  - `dotnet test TenantWright.slnx` (see [Testing](testing.md))
  - `pwsh ./scripts/verify-eto-placement.ps1`
  - Start the AppHost and request a token with the new scope.
- **Repository index.** `scripts/generate-repo-index.ps1` regenerates `backend/index/`. That folder is not checked in at present.
