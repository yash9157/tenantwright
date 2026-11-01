# Troubleshooting

## A resource shows "The build failed"

Aspire rebuilds stale projects when it starts them. Because several services start in parallel, these rebuilds can contend for the same shared `bin`/`obj` outputs. File locks, often taken by antivirus scanning freshly written DLLs, then fail the build.

1. Stop the AppHost.
2. Run `dotnet build TenantWright.slnx` from `backend/` until it succeeds.
3. Start the AppHost again, or use **Restart** on the failed resource in the dashboard.

If it keeps happening, exclude the repository folder from real-time antivirus scanning.

## The first run is slow

The first start pulls the PostgreSQL, Valkey, RabbitMQ and Temporal images, which can take several minutes. Services wait (`WaitFor`) until their database, Valkey and broker are healthy. Later starts reuse the persistent containers.

## I can't find the port for PostgreSQL, Valkey or RabbitMQ

Infrastructure host ports are allocated dynamically, so read them from the dashboard. To keep one stable for a desktop tool, pin it with an AppHost setting:

- `LocalInfrastructure:Postgres:Port`
- `LocalInfrastructure:Valkey:Port`
- `LocalInfrastructure:RabbitMq:Port`, `LocalInfrastructure:RabbitMq:ManagementPort`
- `LocalInfrastructure:Temporal:Port`, `LocalInfrastructure:Temporal:UiPort`

See [Getting started](getting-started.md#what-the-apphost-starts).

The service ports (5161–5166, 5234, 5137 and their HTTPS pairs) are fixed. If another process holds one of them, that service fails to bind.

## A token request returns `invalid_grant`

Auth returns the same generic description for every failure. Check, in order:

1. The request includes `tenant_id` (or `tenant`). Password-grant requests without one are rejected.
2. The user belongs to that tenant. `hostadmin` is a host user and cannot use the password grant.
3. The password is the current value of `Parameters:bootstrap-tenant-admin-password` (`dotnet user-secrets list --project orchestration/TenantWright.AppHost`).
4. The app runs in Development. Outside Development the password grant is disabled.

The failure reason is recorded as an audit event (`auth.token.failure`).

## Calls succeed but return 403

- Request the `roles` scope. Without it the token carries no role claims, so role-based grants never match.
- Request the scope that matches the target service: `identity`, `tenant`, `permissions` and so on.
- Check that the call is not sending a conflicting `X-Tenant-Id` header to the service directly. The gateway strips it; a direct call does not.

## The AppHost fails with a messaging error

- **`LocalMessaging:Provider ... allowed only when the AppHost runs in Development`.** Local brokers are development-only. Use `None` for other environments.
- **`MassTransitLicensePath`.** The licence file must exist, be given as an absolute path, and be marked read-only.
- **Malformed file in the local messaging material.** Stop the AppHost and delete `%LOCALAPPDATA%\TenantWright\local-messaging`. Then remove the `tenantwright-rabbitmq` container and its `tenantwright-rabbitmq-data` volume, and start again. All broker credentials and signing keys are regenerated.

## A service fails at startup with a configuration error

Startup validation is intentional (see [Security](security.md#fail-closed)). The message names the key and usually how to fix it, for example `AuthServer:Authority`, `ConnectionStrings:Default`, `Messaging:*` or `Caching:Valkey:Configuration`. Outside the AppHost, supply those values yourself; see [Configuration](configuration.md#running-one-service-outside-the-apphost).

## Database or login errors after resetting secrets

PostgreSQL, Valkey and RabbitMQ volumes keep the credentials they were created with. If you cleared the AppHost user secrets or the messaging material, remove the matching containers and volumes, then start again. See [Getting started](getting-started.md#stop-and-reset).

## Audit seals are not being written

AuditLogs skips sealing, and logs a warning, when `AuditLogging:Signatures:CurrentKeyId` has no key. The AppHost configures `auditlogs-local-v1`. Outside the AppHost, set both `AuditLogging:Signatures:CurrentKeyId` and `AuditLogging:Signatures:Keys:<id>`.

## Onboarding or other cross-service flows never complete

These flows need messaging and Temporal. With `LocalMessaging:Provider=None` (or `Messaging:Provider=None` on a single service), events are dropped and commands fail. Check the Workflows logs and the Temporal UI endpoint on the dashboard.

## A dedicated tenant database stays unmigrated

The service logs `Dedicated tenant database for <Name> is not migrated` (a warning, at most every 5 minutes per database) and the tenant's work fails closed. The migration reconciler retries on its own; its log line `Reconciling a dedicated tenant database ... failed (<ErrorType>)` names the cause:

- **`TenantDatabaseForeignSchemaException`:** the database already holds another owner's schema. For example, the same physical database was assigned to two services under different host spellings, or reused after another assignment. The migrator refuses it before any DDL. Assign an empty database, or have an operator empty this one.
- **`TenantDatabaseConflictException`:** another tenant uses the same database. Re-point one of them.
- **A PostgreSQL or connection error:** the target is unreachable, or its owner cannot run DDL. Fix the target, and the next attempt (at most 10 minutes later) migrates it.

A `TenantDatabaseChangedEto` that was dead-lettered needs no replay: the reconciler covers it once tenant work reaches the database.

## Identity preference lookups return 401

Notifications calls Identity under the client profile in `IdentityPreferencesClient:ClientProfile` (default `NotificationsInternal`). If the host has no `OpenIddict:Clients:<profile>:ClientId`/`Secret`, the provider logs `Internal access token configuration is incomplete for client profile <profile>`, and the lookup fails closed without calling Identity. Identity accepts only client ids listed in `IdentityInternal:AllowedClientIds`, which must include `tenantwright-notifications-internal`.

If the token is acquired but Identity still answers 401, check the base address scheme. If `IdentityPreferencesClient:BaseAddress` points at Identity's HTTP port while Identity redirects HTTP to HTTPS, `HttpClient` follows the 307 and drops the `Authorization` header. Set `Hosting:HttpsRedirection:Enabled=false` on Identity (the AppHost and charts do this), or use an HTTPS base address.
