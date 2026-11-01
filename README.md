# TenantWright

A multi-tenant .NET 10 microservice template. It has an OAuth2/OIDC auth server, a YARP gateway, six domain services, transactional messaging with signed provenance, and opt-in per-tenant databases. It runs locally with one .NET Aspire command and deploys with Helm.

> **Status:** pre-1.0 template. Expect breaking changes.

## Highlights

- **Auth server** (`app/Auth`): OpenIddict OAuth2/OIDC with authorization code, refresh and client credentials. The password grant is available in Development only.
- **Gateway** (`gateway/ApiGateway`): YARP reverse proxy. It validates tokens before forwarding and applies rate limiting, request timeouts and security headers. It returns 404 for `/gN/internal/*` paths.
- **Services:** Identity, Tenant, Permissions, AuditLogs, Notifications and Workflows (Temporal). Each service owns its database and follows a strict layered project layout.
- **Fail-closed security:** cached permission evaluation backed by Valkey, tenant resolution that rejects ambiguous or mismatched input, and startup validation of security-critical settings.
- **Messaging:** you can choose Wolverine (MIT, the local default), MassTransit 9 (commercial licence) or None. Either broker provider uses a transactional outbox and an inbox, and every message carries an ECDSA P-256 origin attestation per service.
- **Multi-tenancy:** all tenants share a database by default. AuditLogs, Notifications and Permissions can opt into a dedicated database per tenant, with per-tenant migrations and fan-out of background work.
- **Audit logs:** captured through the messaging pipeline and sealed in a hash chain signed with HMAC-SHA256 keys.
- **Local dev with zero setup:** the Aspire AppHost starts PostgreSQL, Valkey, RabbitMQ and Temporal. It generates every local secret on first run.
- **Deployment:** Helm charts with digest-pinned images, environment overlays and preflight guardrails.

## Architecture

```mermaid
flowchart LR
    client([Client]) -->|Bearer token| gw[ApiGateway<br/>YARP]
    client -->|/connect/*| auth[Auth<br/>OpenIddict]
    gw -->|/g6| auth
    gw -->|/g1| identity[Identity]
    gw -->|/g2| tenant[Tenant]
    gw -->|/g3| perms[Permissions]
    gw -->|/g4| audit[AuditLogs]
    gw -->|/g5| notif[Notifications]
    gw -->|/g12| wf[Workflows]

    subgraph infra[Infrastructure]
        pg[(PostgreSQL<br/>one DB per service)]
        valkey[(Valkey<br/>cache, locks, permissions)]
        mq[[RabbitMQ]]
        temporal[[Temporal]]
    end

    auth & identity & tenant & perms & audit & notif & wf --> pg
    auth & identity & tenant & perms & audit & notif & wf --> valkey
    auth & identity & tenant & perms & audit & notif & wf <-->|outbox / inbox| mq
    wf --> temporal
```

## Quick start

You need the .NET 10 SDK and Docker Desktop. See [Getting started](docs/getting-started.md) for details.

1. Trust the ASP.NET Core development certificate: `dotnet dev-certs https --trust`.
2. Start Docker Desktop.
3. `cd backend`
4. `dotnet build TenantWright.slnx`
5. `dotnet run --project orchestration/TenantWright.AppHost`
6. Open the Aspire dashboard URL printed in the console. The first run pulls container images, which takes several minutes.
7. Read the generated sign-in passwords: `dotnet user-secrets list --project orchestration/TenantWright.AppHost`.
8. Request a token and call the gateway. See [Getting started → Call the API](docs/getting-started.md#call-the-api).

## Services and local ports

| Resource | Project | HTTP / HTTPS | Gateway prefix |
|---|---|---|---|
| auth | `app/Auth/Auth` | 5234 / 7239 | `/g6` → `/api` |
| apigateway | `gateway/ApiGateway` | 5137 / 7030 | — |
| identity | `services/Identity/Identity` | 5161 / 7161 | `/g1` → `/api/identity` |
| tenant | `services/Tenant/Tenant` | 5162 / 7162 | `/g2` → `/api/tenant` |
| permissions | `services/Permissions/Permissions` | 5163 / 7163 | `/g3` → `/api/permissions` |
| auditlogs | `services/AuditLogs/AuditLogs` | 5164 / 7164 | `/g4` → `/api/audit-logs` |
| notifications | `services/Notifications/Notifications` | 5165 / 7165 | `/g5` → `/api/notifications` |
| workflows | `services/Workflows/Workflows` | 5166 / 7166 | `/g12` → `/api/workflows` |

Project paths are relative to `backend/`. Infrastructure containers use ports that Aspire allocates; the dashboard shows them.

## Documentation

- [Getting started](docs/getting-started.md): run locally, seeded users, tokens.
- [Architecture](docs/architecture.md): repository layout, services, shared libraries, request flow.
- [Configuration](docs/configuration.md): key settings, and running a single service.
- [Messaging](docs/messaging.md): providers, outbox/inbox, trust, adding contracts.
- [Multi-tenancy](docs/multi-tenancy.md): tenant resolution and dedicated tenant databases.
- [Security](docs/security.md): the security model in summary.
- [Deployment](docs/deployment.md): Helm charts, secrets, prerequisites.
- [Testing](docs/testing.md): test projects, and which suites need Docker or a licence.
- [Adding a service](docs/adding-a-service.md): a step-by-step checklist.
- [Troubleshooting](docs/troubleshooting.md)
- [Task records](docs/task/README.md): the planning convention that agent governance uses.

## Security

Report vulnerabilities privately; see [SECURITY.md](SECURITY.md).

## License

Licensed under the [Apache License 2.0](LICENSE). Redistributions and derivative works must keep the attribution notices in [NOTICE](NOTICE), as section 4(d) of the license requires.

Third-party components keep their own licences. The optional MassTransit provider uses MassTransit 9, which is commercially licensed and needs a licence to run. The default Wolverine provider is MIT-licensed.
