# Development Helm inputs

This is the replacement Azure development cluster's existing configuration,
renamed and grouped by owner. Chart/release identities, databases, PVCs and
approved application images are preserved. It is a single-node development
environment; observability remains excluded.

- `backend/common.values.yaml`: shared application resources, direct PostgreSQL
  runtime configuration and the reviewed connection/replica ceilings.
- `backend/<service>.values.yaml`: the service's non-secret environment settings.
- `backend/messaging.values.yaml`: development messaging selection; the shared
  trust catalog is `../../tenantwright-common/files/messaging.values.yaml`.
- `platform/<component>.values.yaml`: NGINX, cert-manager, Valkey, RabbitMQ
  and Temporal configuration. Platform inputs do not inherit application
  database or resource defaults.
- `images.lock.yaml`: user-approved backend workload digests (eight; Workflows is
  selected with `-InstallWorkflowService`).
- `private/`: ignored, owner-protected operator material. `postgresql.local.yaml`
  holds endpoint allocation and CA references; `backend/` and `platform/` hold
  sensitive overrides; `app-secrets.local.yaml` owns credentials; `postgresql-ca/`,
  `certificates/` and `messaging/` preserve trust/signing material. Never force-add
  anything under this directory or print its contents.

Backend order: chart defaults, common values, shared messaging trust and dev
switch when selected, component values, private component overrides, approved
image digest. Gateway and platform charts receive only their component inputs.
Workflows owns its worker setting in `backend/workflows.values.yaml`.

The provider budget remains twenty connections per instance, with four reserved
for migrations/provider operations and at most sixteen allocated to applications.
Two services at two replicas and four connections per process consume sixteen.
Larger VM capacity does not enlarge that database budget. The verifier compares
private egress CIDRs with current DNS and checks matching CA files; failed DNS
verification fails preflight.

From `backend/`, validate without modifying a cluster:

```powershell
.\scripts\verify-helm-environment-layout.ps1
.\charts\helm-install-all.ps1 -Environment dev -InstallWorkflowService -MessagingProvider MassTransit -InstallCertManager -PreflightOnly
.\charts\helm-install-temporal.ps1 -Environment dev -PreflightOnly
```

The installer requires immutable digests in every environment. It no longer
exposes the image-clearing `-UseLatestImages` switch. Development policy is
selected with `-Environment dev`; dependencies build before rendering. Preflight
does not apply Secrets, install operators or upgrade releases.
