# Deployment

This page summarises the Helm-based deployment under `backend/charts/`. The installer scripts and chart values are authoritative, so read them before deploying. Run every command from `backend/`.

> The charts have been exercised against a single-node development cluster with an external managed PostgreSQL. Treat production use as unproven until you have run the preflight against your own environment.

## Contents of `charts/`

| Path | Purpose |
|---|---|
| `tenantwright-{auth,gateway,identity,tenant,permissions,auditlogs,notifications,workflows}` | One chart per workload. Each has a deployment, service, NetworkPolicy, PDB, config map, secret env, migration Job and a Helm test |
| `tenantwright-common` | Library chart: the migration-Job template, messaging templates, and the non-secret messaging trust catalog `files/messaging.values.yaml` |
| `tenantwright-valkey`, `tenantwright-rabbitmq`, `tenantwright-temporal` | In-cluster platform services. RabbitMQ is operator-based and uses per-principal mTLS client certificates |
| `tenantwright-cert-manager`, `tenantwright-observability`, `tenantwright-telemetry` | Optional platform charts |
| `environments/dev`, `environments/production` | Environment overlays. `production` holds only `*.example` templates |
| `helm-install-all.ps1` | Preflight and install of the application stack |
| `apply-secrets.ps1` | Validate and apply Kubernetes Secrets from a git-ignored file |
| `helm-install-temporal.ps1`, `helm-install-observability.ps1`, `helm-install-telemetry.ps1` | Installers for the optional stacks |
| `install-rabbitmq-operators.ps1`, `configure-rabbitmq-topology-tls.ps1` | RabbitMQ Cluster and Topology operators, plus the CA trust for the topology operator |

PostgreSQL is **external**: no chart deploys it. Workload NetworkPolicies require explicit PostgreSQL egress CIDRs. Production needs a pooled runtime (`PostgreSql__Runtime__Mode` of `TransactionPool` or `SessionPool`) and a reviewed `postgresql-pools.yaml` budget.

## Prerequisites

- `helm`, `kubectl`, and a PowerShell YAML module that provides `ConvertFrom-Yaml`, for example `Install-Module powershell-yaml -Scope CurrentUser`.
- A cluster with the RabbitMQ Operator CRDs installed. Install them with `-InstallRabbitMqOperators`, or separately with `install-rabbitmq-operators.ps1`.
- Container images built from each service's `Dockerfile`, pushed to your registry, and pinned by digest in `images.lock.yaml`. The Dockerfiles are `app/Auth/Auth/Dockerfile`, `gateway/ApiGateway/Dockerfile` and `services/<Svc>/<Svc>/Dockerfile`, and they build from the `backend/` root.
- Externally provisioned PostgreSQL databases: one per service, plus any dedicated tenant databases.
- An ingress controller. The NetworkPolicies need its namespace.

## Environment overlays

`Get-EnvironmentValuesFiles` in `charts/environment-values.ps1` merges values files in this order:

1. `backend/common.values.yaml` (if present)
2. With messaging enabled: `tenantwright-common/files/messaging.values.yaml`, then `backend/messaging.values.yaml` (if present)
3. `backend/<component>.values.yaml` or `platform/<component>.values.yaml` (required)
4. `private/<group>/<component>.values.local.yaml` (if present; git-ignored)

Values containing placeholders (`changeme`, `example.invalid`, `<...>`) are rejected. For production:

- Copy `environments/production/*.example` into your own directory without the `.example` suffix.
- Fill in real values, and pass the directory with `-EnvironmentValuesDirectory`.
- `images.lock.yaml` maps each release to a `sha256:` digest:

  ```yaml
  images:
    tenantwright-auth: sha256:<64 hex>
  ```

## Secrets

`apply-secrets.ps1` reads `environments/<env>/private/app-secrets.local.yaml`, or the file given with `-SecretsFile`. It accepts only `kind: Secret` documents, checks the required names and keys, and rejects Secrets that are managed elsewhere.

```powershell
pwsh ./charts/apply-secrets.ps1 -Environment production -Namespace tenantwright -MessagingProvider Wolverine -SecretsFile <path> -DryRun
```

Required (app profile). `<svc>` stands for each of auditlogs, auth, identity, notifications, permissions, tenant and workflows.

| Secret | Keys | When |
|---|---|---|
| `tenantwright-valkey-auth` | `default`, `replication` | always |
| `tenantwright-<svc>-db` | `connection-string` | always |
| `tenantwright-auth-secrets` | `openiddict-internal-client-secret`, `openiddict-signing-cert-password`, `openiddict-encryption-cert-password` | always |
| `tenantwright-<svc>-rabbitmq` | `rabbitmq-host`, `rabbitmq-port`, `rabbitmq-username`, `rabbitmq-password`, `rabbitmq-vhost` (label `rabbitmq.com/topology-operator: 'true'`) | any provider except `None` |
| `tenantwright-<svc>-messaging-signing` | `private.pem` (ECDSA P-256) | any provider except `None` |
| `tenantwright-messaging-verification-keys` | `tenantwright-<svc>-v1.pem` for each service, plus `tenantwright-messaging-replay-v1.pem` | any provider except `None` |
| `tenantwright-masstransit-license` | `license` | `MassTransit` |

Managed separately, and rejected by the script:

- `tenantwright-auth-openiddict-certs`, which holds `auth-signing.pfx` and `auth-encryption.pfx`
- `tenantwright-auth-tls` and `tenantwright-gateway-tls`, the ingress TLS certificates (issued by cert-manager when enabled)
- `tenantwright-rabbitmq-server-tls` and `tenantwright-rabbitmq-ca`
- `tenantwright-shared-infra`
- the Temporal client TLS and API-key Secrets (`tenantwright-workflows-temporal-*`)

The charts also reference Secrets that `apply-secrets.ps1` does not validate. Check every `secretRefs` entry in each chart's `values.yaml` before installing. Among them:

- `tenantwright-<svc>-audit-signing` (`hmac-v1`)
- `tenantwright-tenant-connection-protection` (`aes-v1`)
- `tenantwright-auditlogs-archive-blob` (`connection-string`)
- `tenantwright-<svc>-rabbitmq-client-tls`
- per-service `*-auth` client secrets

## Install

```powershell
# Render and validate everything without touching the cluster
pwsh ./charts/helm-install-all.ps1 -Environment production -EnvironmentValuesDirectory <dir> -MessagingProvider Wolverine -InstallWorkflowService -PreflightOnly

# Apply
pwsh ./charts/helm-install-all.ps1 -Environment production -EnvironmentValuesDirectory <dir> -MessagingProvider Wolverine -InstallWorkflowService -Wait
```

Parameters:

- `-Environment`: `dev` or `production`. The default is `production`.
- `-MessagingProvider`: `None`, `Wolverine` or `MassTransit`. The default is `None`.
- Optional switches: `-InstallCertManager`, `-InstallRabbitMqOperators`, `-InstallWorkflowService`, `-CreateNamespace`, `-Wait`, `-PreflightOnly` and `-DryRun`.

Releases install in this order:

1. cert-manager (optional)
2. valkey
3. rabbitmq
4. permissions
5. identity
6. tenant
7. auditlogs
8. notifications
9. auth
10. workflows (optional)
11. gateway

The preflight renders every chart and enforces several rules:

- Each workload has a digest from `images.lock.yaml`.
- NetworkPolicy is enabled, with ingress-controller and PostgreSQL egress ownership.
- PostgreSQL pool settings stay within budget.
- Production renders contain no development or placeholder hosts, and platform charts set an explicit `storageClassName`.

Every chart except the gateway runs a migration Job with `EfCore__AutoMigrate` and `EfCore__MigrateAndExit=true` (from `tenantwright-common/templates/_migration-job.tpl`). The Deployments themselves do not migrate. Helm does not roll back database changes when an upgrade fails.

## What you must provide

- **PostgreSQL.** Databases, credentials, CA, egress CIDRs and pool budgets.
- **Valkey.** The chart deploys it; you supply the auth Secret. Clients connect over TLS.
- **RabbitMQ.** The chart deploys it. You supply the operators, the CA and server TLS, and per-service credentials and signing keys.
- **TLS.** Ingress certificates for Auth and the gateway, via cert-manager or your own Secrets.
- **Auth.** Signing and encryption PFX certificates with their passwords.
- **Tenant connection strings.** The encryption key `tenantwright-tenant-connection-protection`.
- **Audit logs.** Signing keys for audit events (`*-audit-signing`).
- **Workflows.** The Temporal endpoint and its TLS or API key. The chart defaults to `Temporal__WorkerEnabled: 'false'`; enable it where the onboarding workflow should run.
- **Public URLs.** An HTTPS `AuthServer__Authority`, `OpenIddict` issuer and client redirect URIs that match your public hosts.

## Verification scripts

These scripts in `scripts/` check charts offline:

- `verify-helm-environment-layout.ps1`
- `verify-messaging-charts.ps1`
- `verify-environment-guardrails.ps1`
- `verify-rabbitmq-platform.ps1`
- `verify-temporal-platform.ps1`
- `verify-cert-manager-platform.ps1`
- `verify-postgresql-pooling.ps1`

There is no CI workflow in the repository yet.
