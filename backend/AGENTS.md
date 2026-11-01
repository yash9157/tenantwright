# Backend Governance

This file governs the `backend/` backend. Apply the workspace-root `AGENTS.md`
first, then this file, then the `tenantwright-*` skill for the affected boundary.

## Core Rules

- Fail-closed by default; secure over convenient.
- Deterministic over implicit; no hidden side effects.
- Async-first hot paths; production-grade over demo-grade.

## Topology

Verify against `TenantWright.slnx` before relying on this list.

- `app/Auth` — OpenIddict 7 authorization server (Razor pages, Quartz pruning).
- `gateway/ApiGateway` — public YARP gateway.
- `services/` — Identity, Tenant, Permissions, AuditLogs, and Notifications
  use the six-project shape: host, `Application`, `Application.Contracts`,
  `Domain`, `Domain.Shared`, `EntityFrameworkCore`. Workflows has four
  projects (host, `Application`, `Application.Contracts`,
  `EntityFrameworkCore`) and runs the Temporal initial-owner onboarding
  workflow. Do not normalize Workflows without an approved ownership decision.
- `shared/Shared.*` — infrastructure-only libraries.
- `orchestration/TenantWright.AppHost` — Aspire AppHost: persistent PostgreSQL,
  Valkey, RabbitMQ, and Temporal containers with generated local secrets and
  messaging material; `orchestration/TenantWright.ServiceDefaults` — OpenTelemetry,
  health, and service discovery defaults.
- `tests/` — xUnit projects (EF InMemory, SQLite, and Testcontainers as the
  boundary requires), including `TenantWright.AppHost.Tests`.
- `charts/` — Helm charts and `charts/environments/{dev,production}` overlays.
- `scripts/`, `tools/` — verification scripts, repo indexer, deployment and
  migration-lineage tooling (`tools/deployment`).

## Backend Escalation Boundaries

- Always ask before crossing or weakening security, tenant isolation, data
  ownership, migration lineage, package policy, service boundaries, or
  `Shared.*` ownership.
- Ask before accepting boundary exceptions, new shared modules, new
  infrastructure packages, hard-blocker exceptions, or follow-up work that is
  unclear, expensive, blocked, or out of scope.
- Under the root `Project Stage`, breaking contract/schema/DTO changes,
  replacing an unshipped and unapplied migration draft, and dropping backward
  compatibility do not require asking. Resetting a persistent database still
  requires explicit approval and exact target verification.

## Coding Standards

- Keep the service layout, feature-first folders, and folder-aligned
  namespaces.
- Layer ownership is strict: contracts and DTOs in `*.Application.Contracts`,
  use-case orchestration in `*.Application`, invariants in domain types,
  shared constants and error codes in `*.Domain.Shared`, and EF provider work in
  `*.EntityFrameworkCore`.
- Hosts and controllers stay thin: they call application contracts or
  same-service host helpers, never own business rules or EF queries.
- Do not create interfaces only because a class is injected. Use
  `*.Application.Contracts` interfaces for public app services, cross-boundary
  contracts, multiple implementations, or decorators.
- Prefer generic repositories for ordinary CRUD. `*.Application` may use EF
  Core and its own `*.EntityFrameworkCore` for owned queries and projections; do
  not add custom repository layers by default.
- Public APIs are async, tenant-owned entities implement `IMultiTenant`, and
  application outputs are contract DTOs, never domain entities.

### Error Codes and Exceptions

- Service business error codes live only in the owning `*.Domain.Shared`
  `*ErrorCodes` class; generic infrastructure codes only in `SharedErrorCodes`.
- Pass canonical members directly (for example
  `AuditLogsErrorCodes.AuditLogNotFound`). No local constants, copies, wrappers,
  or aliases of an error-code value.
- Use the typed `Shared*Exception` family (`SharedValidationException`,
  `SharedConflictException`, `SharedForbiddenException`, ...) for
  caller-correctable failures. Reserve `InvalidOperationException` for genuine
  internal invariant or programming failures.

## Backend Skill Map

- `tenantwright-repo-governance` — cross-boundary governance, skills, sign-off, Quality Gates.
- `tenantwright-brainstorming` — uncertain features, workflows, or architecture choices.
- `tenantwright-service-scaffolding` — service structure, references, packages, Dockerfiles.
- `tenantwright-service-runtime` — DI, options, Valkey, messaging runtime, tenancy, health, telemetry.
- `tenantwright-unit-of-work` — UoW, DbContext ownership, and transactions.
- `tenantwright-background-processing` — Hangfire, Quartz, Temporal, and hosted workers.
- `tenantwright-api-event-contracts` — HTTP DTOs, OpenAPI, `*Eto` contracts, topology.
- `tenantwright-postgresql-efcore` — PostgreSQL, EF Core, migrations, queries, tenant databases.
- `tenantwright-auth-identity-flow` — Auth, Identity, MFA, sessions, and tokens.
- `tenantwright-permissions-authorization` — permissions, grants, features, and policies.
- `tenantwright-testing-strategy` — test structure, fixtures, and verification.
- `tenantwright-platform-ops` — Aspire AppHost, images, Helm, and deployment tooling.
- `tenantwright-observability-operations` — telemetry, dashboards, alerts, and runbooks.
- `tenantwright-documentation` — substantial documentation under workspace-root `docs/`.
- `microbenchmarking` — scoped BenchmarkDotNet comparisons; not SQL or load-test proof.
- `dotnet-trace-collect` — explicitly authorized diagnostic collection only.

## Architecture and Boundaries

- No direct cross-service implementation project references. Cross-service
  wire contracts live in `Shared.EventContracts` (`*Eto`) or a service's
  `*.Application.Contracts`; HTTP calls between services use internal
  endpoints and M2M tokens.
- Prevent circular dependencies and convenience references. Validate new
  services, projects, references, and packages with
  `tenantwright-service-scaffolding`; `docs/adding-a-service.md` describes the
  blueprint.

## Messaging

- `Messaging:Provider` selects exactly one transport per host: `Wolverine`,
  `MassTransit`, or `None`. The code and service `appsettings.json` default to
  `None` (bus-free: events dropped, commands fail closed, audit to the
  structured log). The AppHost passes `LocalMessaging:Provider`, which defaults
  to `Wolverine`, in Development only. Never substitute a provider silently.
- `Shared.EventBus` is the provider-neutral core: `ITenantWrightMessageSender`,
  `ITenantWrightMessageHandler<T>` (invoked after trust verification and tenant
  restoration, inside the consuming unit of work), signed attestations under
  `Messaging:Trust`, protected replay, and `TenantWrightMessagingTopology` — the one
  reviewed catalog of routes, `tenantwright-<service>` producer principals, and
  endpoint ownership.
- The durable outbox/inbox is provider-neutral and lives in
  `Shared.EntityFrameworkCore/Messaging` (`AddTenantWrightMessagingPersistence`,
  `AddTenantWrightMessagingEntities`); write messages through the owning unit of
  work and keep handlers idempotent.
- `Shared.EventBus.Hosting` (`AddTenantWrightMessagingHost<TDbContext>`) composes the
  selected provider. Provider adapters own transport only, never trust, tenancy,
  persistence, or contracts.

## Multi-Tenancy

- Tenant-owned data uses the shared service database by default. Only the
  names in `TenantWrightDatabaseCatalog.TenantDedicatable` (AuditLogs,
  Notifications, Permissions) may move a tenant to a dedicated database, managed
  through the Tenant API `api/tenant/tenants/{id}/databases`. Auth, Identity,
  and Tenant DbContexts are host-only; Workflows owns no tenant data.
- Cross-tenant background work runs per database through `TenantDatabaseFanOut`;
  dedicated databases are migrated by `TenantDatabaseMigrator`. Never assume a
  single database when a context is dedicatable.
- Require explicit `TenantId` or current-tenant enforcement for tenant-owned
  data. Do not add host fallback when tenant context exists. Cross-tenant access
  must be intentionally authorized and audited.
- Keep cache keys, distributed locks, messages, jobs, logs, and metrics
  tenant-safe. `docs/multi-tenancy.md` describes the model.

## Package Policy

- No ABP packages (`Volo.Abp.*`, `Abp.*`, or other ABP-distributed packages).
  Use in-repo `Shared.*` infrastructure instead.
- MassTransit packages are referenced only by `Shared.EventBus.MassTransit`;
  MassTransit 9 requires a commercial licence (`MT_LICENSE` or
  `MT_LICENSE_PATH`). Wolverine packages are referenced only by
  `Shared.EventBus.Wolverine`. `Shared.EventBus` references no transport.
- Versions are centrally managed in `Directory.Packages.props` (transport and
  Temporal versions in `Directory.Build.props`). Preview packages, new
  infrastructure packages, or duplicate infrastructure libraries require
  explicit user approval.

## Shared Infrastructure

- `Shared.*` is infrastructure-only: no service business rules, service
  permissions, DTOs, or workflows.
- Do not duplicate unit of work, audit, caching, tenancy, messaging,
  distributed locks, or data filters.
- Keep shared behavior async, deterministic, tenant-safe, and explicitly
  registered. Route changes through `tenantwright-service-runtime`, with
  `tenantwright-unit-of-work` for transactions and `tenantwright-background-processing`
  for jobs and workers.

## Security

- Return `401`/`403` or fail startup when auth, tenant, session, permission, or
  security-critical configuration is uncertain.
- Never log tokens, secrets, passwords, connection strings, message bodies,
  personal data, or protected tenant data. Never print generated local secrets.
- Review every endpoint, query, cache key, event, worker, job, and export for
  tenant isolation. Never weaken security or isolation to satisfy tests.
- Route auth, session, token, and MFA work through
  `tenantwright-auth-identity-flow`, and permission definitions, grants, evaluation,
  and policies through `tenantwright-permissions-authorization`.

### Authorization Policies

- Plain `[Authorize]` suits current-user APIs when authentication suffices and
  audience/scope already constrains callers.
- Use named host policies for management, internal, M2M, or
  scope/client-constrained endpoints, registered explicitly in the host;
  otherwise the shared permission policy provider may treat the name as a
  permission check.
- Use `Shared.Permissions` policies for business capabilities defined in a
  service's `*Permissions` class, such as `AuditLogs.Read` or
  `Permissions.Management.Grants`. Inspect the actual definitions and
  evaluator; a dotted name or parent grant is not proof of read-only access.
- Do not protect permission bootstrap endpoints with permission policies; the
  current user's permissions must be discoverable without a grant.
- For HTTP-only operations the controller policy is normally the authorization
  owner; keep application-boundary checks for non-controller callers and
  resource-dependent decisions. Before removing a repeated check, trace callers,
  policies, and denial tests.
- A future frontend must gate on the exact backend policy; UI gates are never a
  security boundary.

### Service-To-Service Tokens

- A downstream call made while handling a user request does not re-authorize
  the user; the entry service's policy does.
- Internal calls on the caller's own authority use client-credentials tokens
  (`IInternalAccessTokenProvider` pattern) against internal endpoints guarded by
  an integration-service plus client-scope policy (for example
  `IdentityInternal`: internal scope, allow-listed `client_id`, and
  `subject == client_id`).
- Never forward a user JWT to another service's internal endpoint; doing so
  forces weakening audience, scope, or subject checks and is a hard blocker.
  When the downstream needs the user's identity, design it as OAuth 2.0 Token
  Exchange (RFC 8693) at Auth; the flow is not enabled for clients today, so
  treat enabling it as an escalated security change.
- Reuse the cached, lock-protected token acquisition (`InternalAccessTokenProvider`,
  `RemoteFeatureAccessTokenProvider`); never add an uncached per-call token
  provider on a hot path.

## Data and Migrations

- Watch for N+1 queries, unnecessary round trips, and client-side evaluation.
  Parameterized raw SQL, stored routines, or PostgreSQL extensions are allowed
  when justified; raw SQL does not inherit EF filters, so tenant boundaries must
  be explicit. Follow `tenantwright-postgresql-efcore`.
- Preserve tenant and soft-delete query filters; bypass only intentionally and
  with review. Back performance indexes with workload evidence; add correctness
  constraints from the start.
- Migrations are generated per DbContext with the local tool
  (`dotnet tool restore`, then `dotnet ef migrations add ...`). Review the
  migration, designer, snapshot, and SQL.

### Migration Lineage Safety

- Before changing migration history, compare source migration IDs with the
  target's applied `__EFMigrationsHistory` IDs, including every dedicated tenant
  database in scope, and check whether the migration shipped in a release. Never
  expose connection strings while doing so.
- Never edit, delete, rename, reorder, or regenerate an applied or released
  migration. Append a corrective migration. Only a newly added, unshipped, and
  proven-unapplied latest migration may be regenerated.
- A persistent database reset or lineage squash requires explicit approval,
  exact environment/database/DbContext verification, and one bounded operation
  that replaces the lineage, resets the database, applies the full chain, and
  verifies history, schema, and service health.
- Never hide drift with conditional DDL, manual `__EFMigrationsHistory` edits,
  or weakened constraints. `tools/deployment/verify_migration_lineage.py` checks
  lineage before deployment.

## Verification

Run from `backend/`:

```powershell
dotnet build TenantWright.slnx
dotnet test TenantWright.slnx
dotnet test tests/Shared.EventBus.Tests/Shared.EventBus.Tests.csproj   # focused example
```

- Prefer focused `dotnet test tests/<Project>/<Project>.csproj` runs for the
  touched boundary, then the solution build. Tests using Testcontainers
  (`Shared.EntityFrameworkCore.Tests`, `Shared.EventBus.Wolverine.Tests`,
  `Shared.Valkey.IntegrationTests`) need a container runtime; report when they
  are skipped.
- `dotnet run --project orchestration/TenantWright.AppHost/TenantWright.AppHost.csproj`
  starts the local stack and requires explicit user approval, as does
  `scripts/run-auth-token-permission-smoke.ps1`.
- `scripts/verify-*.ps1` hold focused static checks (for example
  `verify-eto-placement.ps1`, `verify-messaging-charts.ps1`,
  `verify-helm-environment-layout.ps1`); run the ones matching the change.
- Full Quality Gates (`scripts/run-quality-gates.ps1`) run only when the user
  explicitly says `Run Quality Gates`; details live in
  `tenantwright-repo-governance`.
- No CI workflows and no Postman collections exist in this repository.

## Repo Index

`scripts/generate-repo-index.ps1` runs `tools/RepoIndexer` and writes a JSON map
of projects, services, routes, contracts, and persistence to `index/`. The index
is optional and not tracked; generate it locally when it helps discovery and do
not commit it unless the team decides to track it. Source, migrations,
contracts, and tests always override the index and docs.

## Change Management

When adding projects, changing references or packages, introducing `Shared.*`
modules, or changing solution or module structure:

- Validate architecture, dependency direction, `Shared.*` boundaries, tenancy,
  security, performance, and data rollout impact.
- Verify affected services, routes, contracts, events, charts, tests, and
  runtime wiring (AppHost and `TenantWright.AppHost.Tests` when local composition
  changes).
- Sync affected `docs/` pages, task records, governance, and owning skills.

## Hard Blockers

Refuse changes that introduce ABP dependencies, break tenant isolation, add
implicit fallback in auth or tenant paths, weaken fail-closed behavior, add
undocumented shared infrastructure, reference a transport package outside its
provider project, reduce security to satisfy tests, introduce forbidden project
references or circular dependencies, or normalize known drift as the standard.
Escalate before proceeding when a request appears to require an exception.
Declining or thinning requested work is governed by the root `Engineering
Level` rules.
