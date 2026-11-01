# Shared Module Rules

## General

- Keep `Shared.*` provider-neutral where a provider adapter already exists.
- Keep registration explicit, deterministic, and lifetime-safe.
- Validate critical options at startup; never use permissive fallback for tenant, authorization, transport, or connection decisions.
- Keep hot paths asynchronous and cancellation-aware.
- Do not duplicate caching, locking, UoW, messaging, tenancy, current-user, audit, blob, or permission infrastructure in services.

## Ownership Families

- `Shared.Modularity`, `Shared.Autofac`, and `Shared.AspNetCore.*` own bootstrap and host mechanics.
- `Shared.Domain*`, `Shared.Data`, `Shared.Uow`, and `Shared.EntityFrameworkCore` own domain/data primitives, connection resolution, transactions, repositories, and provider integration.
- `Shared.MultiTenancy`, `Shared.Security`, `Shared.CurrentUser`, and `Shared.Permissions*` own generic tenant, claim, principal, and permission mechanics.
- `Shared.Caching`, `Shared.DistributedLocking`, and `Shared.BlobStoring` own distributed coordination and storage abstractions.
- `Shared.EventBus` owns provider-neutral messaging; `Shared.EventBus.Hosting` selects the provider; the MassTransit and Wolverine projects are adapters only.
- `Shared.EventContracts` owns extracted transport-safe `*Eto` contracts and source-generated JSON metadata.
- `Shared.AuditLogs` owns generic audit emission, not service audit policy.

## Messaging Boundary

Do not couple services to provider packages. Only `Shared.EventBus.MassTransit` may reference MassTransit packages, and only `Shared.EventBus.Wolverine` may reference Wolverine packages. Durable neutral inbox/outbox state belongs to `Shared.EntityFrameworkCore/Messaging`.

## Blockers

Block service policy in `Shared.*`, duplicate infrastructure, tenant-unsafe keys or messages, hidden registration, provider leakage into the neutral core, and fail-open fallback.
