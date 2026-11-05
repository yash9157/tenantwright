# Security model

This page summarises the security design. The source is authoritative; each section points to the code that enforces it.

## Fail closed

- **Startup validation.** Missing or unsafe security configuration stops the host. Examples:
  - a non-HTTPS `AuthServer:Authority` outside Development;
  - a missing `ConnectionStrings:Default`;
  - the password grant enabled outside Development (`AuthPasswordFlowPolicy`);
  - invalid messaging trust or TLS settings (`MessagingOptionsValidator`);
  - insecure broker transport outside Development.
- **Requests.** Uncertain tenant input is rejected with 400 or 403 (see [Multi-tenancy](multi-tenancy.md#tenant-resolution)). Missing permissions deny.
- **Features.** The remote feature check is configured with `FailOpen=false`.
- **Messaging.** With `Messaging:Provider=None`, commands throw rather than silently disappearing. Message verification failures are never retried as transient errors.

## Authentication

- **Auth server.** Auth (`app/Auth/Auth`) is the OpenIddict server.
  - Interactive clients use authorization code + refresh.
  - The password grant is permitted only in Development, and only with an explicit tenant.
  - Token failures are audited with a generic `invalid_grant` description, so callers learn nothing about whether the user or the tenant was wrong.
- **Token validation.** Each service validates tokens for its own audience with OpenIddict validation. The gateway validates first, for the union of service audiences. Every proxied route requires an authenticated user, except the `notificationsWebhooks` route, which is explicitly anonymous.
- **Sessions.** Tokens carry a session id. `SessionValidation` (strict by default) checks a server-side session marker, so revoked sessions stop working before the token expires.
- **Service-to-service calls** use client-credentials tokens. The clients are `auth-internal` and `tenantwright-notifications-internal`, with internal scopes such as `identity-internal` and `features.check`. Internal endpoints (`api/<service>/internal/*`) require those scopes and an allow-listed client. The gateway returns 404 for any `/gN/internal/*` or `/gN/integration-api/*` path, and strips `X-Tenant-Id`, `X-Internal-*` and client-certificate headers.
- **Signing and encryption certificates.** Outside Development, `OpenIddict:SigningCertificate` and `OpenIddict:EncryptionCertificate` must point to PFX files with passwords. Development uses OpenIddict's development certificates. `backend/scripts/generate-auth-certs.ps1` creates self-signed PFX files for non-production use.

## Authorization

- **Definitions.** Permission names are defined per service in `*.Domain.Shared/Permissions` (for example `TenantPermissions`, `AuditLogsPermissions`) through `PermissionDefinitionProvider` classes. The Permissions service aggregates every provider into the catalog (`PermissionsApplicationModule`).
- **Grants.** Grants are stored and managed by the Permissions service, which publishes cache updates over messaging.
- **Evaluation.** Each service evaluates `Shared.Permissions` policies locally with [`ValkeyPermissionChecker`](../backend/shared/Shared.Permissions/Authorization/ValkeyPermissionChecker.cs). Its inputs are user and role grants, keyed by tenant and invalidated by stamps. Precedence is:
  1. explicit user deny
  2. user wildcard deny
  3. user allow
  4. role deny
  5. role allow
  6. parent inheritance

  A permission that requires a feature is denied while that feature is off for the tenant. A missing user id or malformed role claims deny everything.
- **Roles scope.** Role claims are issued only when the token requests the `roles` scope. Without it, role-based grants do not apply.
- **Enforcement.** UI checks are never a security boundary. Every endpoint enforces its policy on the server.

## Tenant isolation

- **Data.** Tenant-owned entities implement `IMultiTenant`, and the EF Core query filter limits queries to the current tenant.
- **Distributed state.** Cache keys, permission keys and distributed locks include the tenant.
- **Messages.** Each message is signed with a scope and tenant id. Consumers verify them and restore the tenant before running the handler.
- **Dedicated databases.** They are encrypted at rest in the Tenant database and bound to tenant + name. They are rejected if they would point at another assignment or at a shared service database, compared on a normalized target. The owning service also fails the tenant closed if the target is its own shared database, and never migrates a database that already holds another service's schema. See [Multi-tenancy](multi-tenancy.md#dedicated-database-api).

## Messaging trust

See [Messaging → Trust model](messaging.md#trust-model). In brief:

- each service has its own broker user and ECDSA P-256 signing key;
- consumers verify origin, route authorization, freshness and the broker-validated `user_id`;
- keys can be revoked.

Wolverine signed dead-letter recovery uses the endpoint owner's terminal signature
and the separate `tenantwright-messaging-replay` broker identity and signing key.
The operator must supply explicit authorization, durable audit and terminal lookup
adapters. Replay IDs are issued and consumed in the destination service's shared
database; tenant context and business inbox deduplication remain enforced. An
authorization permits one consumer attempt. A failed attempt requires a new
authorization and ID, and expired origins, revoked keys and unsigned quarantine
cannot be replayed. See [Terminal custody and protected replay](messaging.md#terminal-custody-and-protected-replay)
for composition and failure recovery.

## Audit logs

- **Capture.** Every service captures audit records (`Shared.AuditLogs`) and sends them to AuditLogs as a command. With messaging disabled they are written to the structured log instead.
- **Event signatures.** Audit events can be signed with HMAC-SHA256 using `AuditLogging:Signatures` (`CurrentKeyId`, `Keys:<id>`). The charts set `RequireSignatures=true`.
- **Sealing.** AuditLogs seals persisted rows into an append-only hash chain (`AuditLogSeal`). Each seal links to the previous one and is HMAC-signed with the current key. With no key configured, sealing is skipped and a warning is logged. The AppHost generates a local key, `auditlogs-local-v1`. Range verification detects modified rows, missing rows and broken links.

## Secrets

- **Never committed.**
  - The `appsettings.json` files hold only empty placeholders.
  - The root `.gitignore` excludes `*.pfx`, `*.pem`, `*.key`, `.env*` and `charts/environments/*/private/`.
  - `backend/.gitleaks.toml` configures secret scanning.
- **Local credentials are generated.** The AppHost generates passwords, client secrets and keys on first run and stores them in the AppHost user secrets. Broker credentials and signing keys go to `%LOCALAPPDATA%\TenantWright\local-messaging`, which must be outside the repository. See [Getting started](getting-started.md#generated-secrets).
- **Deployed environments** get secrets from Kubernetes Secrets, applied by `charts/apply-secrets.ps1` from git-ignored files. See [Deployment](deployment.md).
- **Logging.** Logs carry hashed tenant identifiers and exception type names rather than connection strings, tokens or payloads.

## Reporting a vulnerability

The repository has no `SECURITY.md` yet. Until one exists, report vulnerabilities privately to the maintainers rather than in a public issue.
