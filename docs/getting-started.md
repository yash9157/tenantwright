# Getting started

Run commands from `backend/` unless a step says otherwise.

## Prerequisites

- **.NET 10 SDK.** The repository has no `global.json`, so any .NET 10 SDK works. Every project targets `net10.0`. The local Aspire SDK is `Aspire.AppHost.Sdk/13.4.6` (see [`TenantWright.AppHost.csproj`](../backend/orchestration/TenantWright.AppHost/TenantWright.AppHost.csproj)).
- **Docker Desktop**, running. The AppHost starts every infrastructure dependency as a container.
- **A trusted HTTPS development certificate:** `dotnet dev-certs https --trust`.
- **Optional:** `psql` or another PostgreSQL client, plus the `dotnet-ef` local tool (`dotnet tool restore`; it is pinned in [`dotnet-tools.json`](../backend/dotnet-tools.json)).

## Run everything

```powershell
cd backend
dotnet build TenantWright.slnx
dotnet run --project orchestration/TenantWright.AppHost
```

Build the solution before you start the AppHost. Otherwise Aspire rebuilds every stale project in parallel, which can fail on file locks (see [Troubleshooting](troubleshooting.md#a-resource-shows-the-build-failed)).

The console prints the Aspire dashboard URL, including its login token. With the default `https` launch profile the dashboard is at `https://localhost:17222`.

### What the AppHost starts

The AppHost is configured in [`AppHost.cs`](../backend/orchestration/TenantWright.AppHost/AppHost.cs) and [`LocalInfrastructure.cs`](../backend/orchestration/TenantWright.AppHost/LocalInfrastructure.cs).

| Container | Image | Purpose |
|---|---|---|
| `tenantwright-postgres` | Aspire PostgreSQL | One database per service: `tenantwright_auth`, `tenantwright_identity`, `tenantwright_tenant`, `tenantwright_permissions`, `tenantwright_auditlogs`, `tenantwright_notifications`, `tenantwright_workflows` |
| `tenantwright-valkey` | Aspire Valkey (password protected) | Cache, distributed locks, permission cache |
| `tenantwright-rabbitmq` | `rabbitmq:4.3.6-management` | Broker. Only started when messaging is enabled |
| `tenantwright-temporal` | `temporalio/temporal:1.9.1` (`server start-dev`) | Workflow engine. Started by default whenever messaging is enabled |

- **Lifetime.** The containers are persistent and use named volumes (`tenantwright-postgres-data`, `tenantwright-valkey-data`, `tenantwright-rabbitmq-data`, `tenantwright-temporal-state`). Data survives AppHost restarts.
- **Host ports.** Aspire allocates the host ports dynamically; read them from the dashboard. To pin a port, set an AppHost setting:
  - `LocalInfrastructure:Postgres:Port`
  - `LocalInfrastructure:Valkey:Port`
  - `LocalInfrastructure:RabbitMq:Port`
  - `LocalInfrastructure:RabbitMq:ManagementPort`
  - `LocalInfrastructure:Temporal:Port`
  - `LocalInfrastructure:Temporal:UiPort`

  For example:

  ```powershell
  dotnet user-secrets set "LocalInfrastructure:Postgres:Port" "55432" --project orchestration/TenantWright.AppHost
  ```
- **Service ports.** Every service gets fixed ports, listed in the [README](../README.md#services-and-local-ports). Each service starts with `ASPNETCORE_ENVIRONMENT=Development` and `EfCore:AutoMigrate=true`, so it applies its own migrations on startup.
- **Internal HTTP.** As in the Helm charts, TLS terminates at Auth and the gateway. The gateway and Notifications reach the six backend services over HTTP, so the AppHost sets `Hosting:HttpsRedirection:Enabled=false` on those services (`AsInternalService()`). Each service's HTTPS port still works for direct calls. Auth and the gateway keep redirecting HTTP to HTTPS.

### Generated secrets

The AppHost generates these parameters on first run and persists them to the AppHost user secrets ([`LocalSecrets.cs`](../backend/orchestration/TenantWright.AppHost/LocalSecrets.cs)). Values you set yourself always take precedence.

| Parameter | Used for |
|---|---|
| `bootstrap-host-admin-password` | Seeded host administrator |
| `bootstrap-tenant-admin-password` | Seeded tenant administrator |
| `internal-client-secret` | `auth-internal` client-credentials client |
| `notifications-internal-client-secret` | `tenantwright-notifications-internal` client |
| `notifications-provider-protection-key` | AES-256 key for stored notification-provider secrets |
| `public-registration-hash-key` | Self-service tenant registration |
| `auditlogs-seal-signing-key` | Audit seal signing key (`auditlogs-local-v1`) |

To list the generated values:

```powershell
dotnet user-secrets list --project orchestration/TenantWright.AppHost
```

The dashboard's parameter view also shows them.

Local messaging material is generated once and stored outside the repository ([`LocalMessaging.cs`](../backend/orchestration/TenantWright.AppHost/LocalMessaging.cs), [`LocalMessagingMaterial.cs`](../backend/orchestration/TenantWright.AppHost/LocalMessagingMaterial.cs)). It lives in `%LOCALAPPDATA%\TenantWright\local-messaging`, which you can override with `LocalMessaging:MaterialPath`; the path must be absolute and outside the git working tree. The directory holds:

- `broker-admin.password`, for the RabbitMQ management user `local-admin`.
- `principals/<principal>/broker.password`, `signing-key.pem` and `signing-key.public.pem` for each service principal.
- `rabbitmq/definitions.json` and `rabbitmq/tenantwright-local.conf`. The broker imports these on every boot.

## Seeded users

| User | Scope | Tenant |
|---|---|---|
| `hostadmin` (`hostadmin@example.local`) | Host | — |
| `tenantadmin` (`tenantadmin@example.local`) | Tenant | `11111111-1111-1111-1111-111111111111` (name `default`) |

The passwords are the `Parameters:bootstrap-host-admin-password` and `Parameters:bootstrap-tenant-admin-password` user secrets. Identity resets them on each seed (`ResetPasswordOnSeed=true`).

## Call the API

In Development, the `tenantwright-gateway` public client may use the password grant ([`AuthPasswordFlowPolicy.cs`](../backend/app/Auth/Auth/Authorization/AuthPasswordFlowPolicy.cs)). Outside Development, enabling the password grant fails startup. The token endpoint has two requirements:

- **A tenant.** Pass `tenant_id` (or `tenant` with the tenant name). A request without a tenant is rejected, so this flow works only for tenant users such as `tenantadmin`. Host users sign in through the interactive authorization-code flow at `/connect/authorize`.
- **The `roles` scope.** Without it the token carries no role claims, so role-based permission grants never apply.

PowerShell:

```powershell
$password = (dotnet user-secrets list --project orchestration/TenantWright.AppHost |
    Where-Object { $_ -like 'Parameters:bootstrap-tenant-admin-password = *' }) -replace '^.*? = ', ''

$token = (Invoke-RestMethod -Method Post -Uri 'https://localhost:7239/connect/token' `
    -ContentType 'application/x-www-form-urlencoded' -Body @{
        grant_type = 'password'
        client_id  = 'tenantwright-gateway'
        username   = 'tenantadmin'
        password   = $password
        tenant_id  = '11111111-1111-1111-1111-111111111111'
        scope      = 'openid profile roles identity tenant permissions'
    }).access_token

Invoke-RestMethod -Uri 'https://localhost:7030/g1/profile' -Headers @{ Authorization = "Bearer $token" }
```

curl (`curl.exe` on Windows):

```powershell
curl.exe -s https://localhost:7239/connect/token `
  --data-urlencode "grant_type=password" `
  --data-urlencode "client_id=tenantwright-gateway" `
  --data-urlencode "username=tenantadmin" `
  --data-urlencode "password=$password" `
  --data-urlencode "tenant_id=11111111-1111-1111-1111-111111111111" `
  --data-urlencode "scope=openid profile roles identity tenant permissions"

curl.exe -s https://localhost:7030/g1/profile -H "Authorization: Bearer <access_token>"
```

`/g1/profile` is forwarded to Identity's `GET /api/identity/profile`. The available scopes are `openid`, `profile`, `email`, `roles`, `identity`, `tenant`, `permissions`, `notifications`, `auditlogs`, `workflows` and `offline_access`.

## Choosing the local messaging provider

The AppHost setting `LocalMessaging:Provider` selects the provider:

| Value | Behaviour |
|---|---|
| `Wolverine` (default) | Starts RabbitMQ |
| `MassTransit` | Starts RabbitMQ. Also needs `LocalMessaging:MassTransitLicensePath`: an absolute path to a **read-only** licence file, passed to services as `MT_LICENSE_PATH` |
| `None` | Runs every service bus-free. Starts no broker. Disables Temporal and public registration by default |

Each provider uses its own virtual host (`tenantwright-development-<provider>`), so switching providers never collides with queues declared by the other one.

```powershell
dotnet user-secrets set "LocalMessaging:Provider" "None" --project orchestration/TenantWright.AppHost
```

See [Messaging](messaging.md) for what each provider does.

## Stop and reset

Stopping the AppHost leaves the persistent containers running. To wipe all local data, stop the AppHost and run the commands below. **This deletes all local data.**

```powershell
docker rm -f tenantwright-postgres tenantwright-valkey tenantwright-rabbitmq tenantwright-temporal
docker volume rm tenantwright-postgres-data tenantwright-valkey-data tenantwright-rabbitmq-data tenantwright-temporal-state
```

If you also clear the AppHost user secrets, remove the volumes as well. The existing data volumes were initialised with the old generated passwords.
