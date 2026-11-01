# Production input examples

This is a template collection, not a configured production deployment. Copy
selected examples into an operator-owned directory, preserving `backend/` and
`platform/`, remove `.example`, and replace every token. Do not copy development
resource, direct-connection or storage settings.

- `backend/<service>.values.yaml.example`: production application policy.
- `platform/<component>.values.yaml.example`: platform storage/resource policy.
- `images.lock.yaml.example`: immutable workload image map.
- `postgresql-pools.yaml.example`: measured allocation and provider reserve.
- `examples/secrets/`: synthetic Secret templates. Actual credentials belong in
  an ignored environment `private/` directory or the approved Secret manager.

From `backend/`, validate complete operator inputs without cluster mutation:

```powershell
.\charts\helm-install-all.ps1 -Environment production -EnvironmentValuesDirectory C:\secure-deploy\tenantwright\production -InstallWorkflowService -MessagingProvider MassTransit -PreflightOnly
.\charts\helm-install-temporal.ps1 -Environment production -EnvironmentValuesPath C:\secure-deploy\tenantwright\production\platform\temporal.values.yaml -PreflightOnly
```

The installer reads `images.lock.yaml` and `postgresql-pools.yaml` from the
environment root. It rejects incomplete inputs, development hosts, invalid
digests, unsupported pooling and oversubscribed budgets. Durable storage and
Secret references require review for the real target. Passing preflight proves
static configuration; it does not apply Secrets, install operators or deploy.

Temporal/observability retain separate namespace and lifecycle owners.
Development observability remains excluded.
