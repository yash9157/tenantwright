# Testing

All tests are xUnit projects under `backend/tests/`. The solution lists 27 of them. Run commands from `backend/`.

```powershell
dotnet build TenantWright.slnx
dotnet test TenantWright.slnx
```

To run a single project, or a single class within it:

```powershell
dotnet test tests/Tenant.Tests/Tenant.Tests.csproj
dotnet test tests/Tenant.Tests/Tenant.Tests.csproj --filter "FullyQualifiedName~TenantDatabaseContractTests"
```

The build treats warnings as errors (`TreatWarningsAsErrors` in `Directory.Build.props`), so analyzer findings fail the build.

## What needs what

| Suite | Persistence / infrastructure | Requirement |
|---|---|---|
| Service tests (`Auth`, `Identity`, `Tenant`, `Permissions`, `AuditLogs`, `Workflows`) | EF Core InMemory and/or SQLite, Moq | none |
| `Notifications.Tests` without container categories | EF Core InMemory/SQLite | none |
| `Notifications.Tests`, `Category=PostgresIntegration` | Disposable PostgreSQL databases | **Docker** |
| `Notifications.Tests`, `Category=ValkeyIntegration` | Disposable Valkey server, independent clients | **Docker** |
| `Gateway.Tests`, `TenantWright.AppHost.Tests` | In-process; static checks of the AppHost wiring and generated broker definitions | none |
| Most `Shared.*.Tests` | In-process | none |
| `Shared.Valkey.IntegrationTests` | Valkey container (Testcontainers) | **Docker** |
| `Shared.EventBus.Wolverine.Tests` (integration classes) | RabbitMQ + PostgreSQL containers | **Docker** |
| `Shared.EntityFrameworkCore.Tests`, classes tagged `Category=PostgresIntegration` | PostgreSQL container | **Docker** |
| `MassTransitRabbitMqIntegrationTests` (`Shared.EventBus.Tests`) and `MassTransitTrustedOutboxIntegrationTests` (`Shared.EntityFrameworkCore.Tests`) | RabbitMQ + PostgreSQL containers | **Docker**, `TENANTWRIGHT_RUN_MASSTRANSIT_RABBITMQ_TESTS=1`, and exactly one of `MT_LICENSE` / `MT_LICENSE_PATH`. Otherwise these tests are skipped |

Only the MassTransit live tests skip themselves. The other container-backed suites expect Docker and fail without it. To run without Docker, exclude them:

```powershell
dotnet test TenantWright.slnx --filter "Category!=PostgresIntegration&Category!=ValkeyIntegration&Category!=RabbitMqIntegration&FullyQualifiedName!~Shared.Valkey.IntegrationTests&FullyQualifiedName!~WolverineRabbitMqIntegrationTests"
```

To run the licensed MassTransit suites, set the environment for the session (never commit a licence):

```powershell
$env:TENANTWRIGHT_RUN_MASSTRANSIT_RABBITMQ_TESTS = '1'
$env:MT_LICENSE_PATH = 'C:\secure\masstransit-license.txt'
dotnet test tests/Shared.EventBus.Tests/Shared.EventBus.Tests.csproj --filter "Category=RabbitMqIntegration"
```

## Architecture and contract guards

Several tests and scripts enforce invariants. When they fail, fix the code rather than relaxing the guard.

- `tests/Shared.EventContracts.Tests`: contract attributes, JSON registration and the catalog.
- `tests/Shared.EventBus.Tests`: topology, trust, options validation, provider configuration, and the inbound pipeline.
- `tests/Shared.EventBus.Wolverine.Tests`: mapper/terminal/replay denial tests plus
  RabbitMQ/PostgreSQL recovery tests. These cover signed failed-handler custody,
  operator authorization, single-use replay IDs, restored tenant context,
  endpoint-specific event replay, failed replay attempts and recovery after the
  terminal exchange disappears, including shutdown and replacement-consumer recovery.
  The tests use isolated vhosts and databases and
  test-only operator policies; they do not configure a deployed recovery tool.
- `tests/TenantWright.AppHost.Tests`: AppHost services, messaging principals and broker definitions stay in step with `TenantWrightMessagingTopology`.
- `scripts/verify-eto-placement.ps1`: `*Eto` types live only in approved assemblies.
- `scripts/verify-messaging-charts.ps1`, `scripts/verify-helm-environment-layout.ps1`, `scripts/verify-environment-guardrails.ps1`: offline chart checks. These need `helm` (`tools/helm.exe` is checked in) and the PowerShell YAML module.

`scripts/run-quality-gates.ps1` runs the full gate set: tests, the ETO placement check, and optional smoke and performance-budget checks. `scripts/run-auth-token-permission-smoke.ps1` and `scripts/verify-security.ps1` call running services, so they need a live stack.

## Maintainer-approved pull request CI

The owner starts CI after reviewing a pull request to `main`. In GitHub Actions,
open **Pull Request Validation**, choose **Run workflow** from `main`, and enter the
PR number and full head commit SHA. The same procedure applies to the owner's
PRs. Review changes to workflow files as part of the PR. Keep repository write
access with the sole maintainer and leave fork-proposed workflows unapproved.

The run builds the solution, runs the test suite on a Docker-capable runner, and
checks event placement and application charts. Licensed MassTransit integration
tests skip under their existing opt-in rules. The workflow uses no repository
secrets. A separate job reports the result for the reviewed commit. After a new
push, review the new commit and start a new run before merging.

## Notification orchestration verification

`Notifications.Tests` covers dispatch admission/idempotency, independent recipient
fallback, acceptance/review/expiry holds, cancellation during preparation, exact API
permissions and sensitive redaction, provider timeout/outcome handling, bounded
archiving, and real PostgreSQL constraints/concurrency/migration backfills.
`NotificationQuotaReservationTests` exercises atomic concurrent reservations from
two Valkey clients, immutable binding, denial without partial consumption and
window rollover. No real notification provider is called.

```powershell
dotnet test tests/Notifications.Tests/Notifications.Tests.csproj --filter "Category!=PostgresIntegration&Category!=ValkeyIntegration"
dotnet test tests/Notifications.Tests/Notifications.Tests.csproj --filter "Category=PostgresIntegration"
dotnet test tests/Notifications.Tests/Notifications.Tests.csproj --filter "Category=ValkeyIntegration"
```

The last two commands start and dispose their own containers. Agents must obtain
the explicit runtime approval required by the workspace instructions first.
