# Platform Operations Map

## Local Orchestration

`backend/orchestration/TenantWright.AppHost/` is the only local orchestrator. It uses Aspire to provision persistent PostgreSQL, Valkey, RabbitMQ, and Temporal resources, generates local secret and messaging material, and enables the local broker only in Development. Starting it requires explicit user approval:

```powershell
dotnet run --project orchestration/TenantWright.AppHost/TenantWright.AppHost.csproj
```

Run that command from `backend/` only after approval.

## Workload Charts

`backend/charts/` contains application charts for:

- `tenantwright-auth`
- `tenantwright-gateway`
- `tenantwright-identity`
- `tenantwright-tenant`
- `tenantwright-permissions`
- `tenantwright-auditlogs`
- `tenantwright-notifications`
- `tenantwright-workflows`

`tenantwright-common` is the shared Helm library chart. Infrastructure charts are `tenantwright-rabbitmq`, `tenantwright-valkey`, `tenantwright-temporal`, `tenantwright-telemetry`, `tenantwright-observability`, and `tenantwright-cert-manager`. Environment overlays are under `backend/charts/environments/dev/` and `backend/charts/environments/production/`; `backend/charts/ci/` contains deployment identity policy.

## Images

Dockerfiles live beside the Auth host, gateway host, and each service host. Verify project publish inputs, non-root execution, health behavior, exposed ports, and runtime-only contents when changing them.

## Deployment Tools

- `backend/tools/deployment/select_changes.py` computes affected image and chart owners and fails safe toward rebuilding when prior automation evidence is unavailable.
- `backend/tools/deployment/assemble_promotions.py` assembles exact immutable digest artifacts from current and certified prior outputs.
- `backend/tools/deployment/deploy_dev.py` preflights complete dev release batches, validates reviewed values and immutable digests, deploys core releases in order, and requests atomic Helm rollback.
- Tests beside these tools cover selection, promotion assembly, dev deployment, gateway chart behavior, and migration lineage.

No checked-in CI workflow currently calls these tools.

## Verification

Use the focused scripts under `backend/scripts/verify-*.ps1`, especially the environment, Helm layout, messaging, certificate, observability, PostgreSQL, RabbitMQ, security, and Temporal checks relevant to the change. Static rendering does not replace an approved live rollout check when runtime behavior is the acceptance condition.
