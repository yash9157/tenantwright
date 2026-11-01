# Package and Shared Usage

## Central Package Policy

`backend/Directory.Packages.props` owns package pins. `Directory.Build.props` owns the MassTransit, Wolverine, and Temporal version properties. Project files normally reference packages without local versions.

ABP packages are forbidden. Preview packages, new infrastructure packages, and overlapping runtime stacks require explicit approval. Add a package only to the layer that directly uses it.

## Layer Placement

- Host projects may own ASP.NET Core, OpenIddict validation, health, and design-time EF tooling. Keep `Microsoft.EntityFrameworkCore.Design` private.
- Application may use capability libraries and EF APIs when a real use case needs them; ordinary orchestration should remain provider-neutral.
- Application.Contracts and Domain.Shared stay dependency-light.
- Domain uses only packages required for domain behavior and never host packages.
- EntityFrameworkCore owns Npgsql and provider-specific EF packages.
- Test packages stay in `tests/**`.

## Existing Shared Owners

Choose by API use, not by copying another service's project file:

- `Shared.AspNetCore.*` for host defaults and ProblemDetails;
- `Shared.Modularity` for module composition;
- `Shared.Domain*`, `Shared.Data`, `Shared.EntityFrameworkCore`, and `Shared.Uow` for data foundations;
- `Shared.Caching` and `Shared.DistributedLocking` for Valkey-compatible caching and locks;
- `Shared.MultiTenancy`, `Shared.CurrentUser`, `Shared.Security`, and `Shared.Permissions*` for tenant and security primitives;
- `Shared.EventBus` for provider-neutral message APIs, `Shared.EventBus.Hosting` for host selection, and `Shared.EventContracts` for cross-service `*Eto` types;
- `Shared.BackgroundTasks` for job abstractions and `Shared.BackgroundTasks.Hangfire` only in hosts that run Hangfire;
- `TenantWright.ServiceDefaults` for common service discovery, resilience, health, and OpenTelemetry composition.

Only `Shared.EventBus.MassTransit` may reference MassTransit packages. Only `Shared.EventBus.Wolverine` may reference Wolverine packages. Services reference the neutral core or host selector, never provider packages directly.

Walk transitive `ProjectReference` closure after every graph change. Docker restore inputs, builds, and architecture review must use that live closure.
