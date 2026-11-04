# Provider-Neutral Messaging Runtime

## Current Architecture

`Messaging:Provider` binds to `TenantWrightMessagingProvider`: `None`, `Wolverine`, or `MassTransit`. The code and service settings default to `None`. `Shared.EventBus.Hosting.AddTenantWrightMessagingHost<TDbContext>` reads the value, selects exactly one installed provider, and fails rather than substituting another provider.

`None` is bus-free: events are dropped, commands fail closed, and audit output goes to structured logs. The Aspire AppHost is development-only composition; its `LocalMessaging:Provider` defaults to Wolverine and passes the selected value to services.

`Shared.EventBus` is the transport-free core. Its stable entry points include `ITenantWrightMessageSender`, `ITenantWrightMessageHandler<T>`, `TenantWrightInboundMessagePipeline`, `TenantWrightMessageCatalog`, `TenantWrightOutboundMessageStamper`, trust attestations, protected replay, options validation, provider preconditions, and metrics. A handler runs only after trust verification and tenant restoration, inside the consuming unit of work.

`TenantWrightMessagingTopology` is the reviewed catalog for contract routes, `tenantwright-<service>` producer principals, endpoint ownership, and allowed producers. Hosts supply only receive endpoints assigned to their principal.

## Provider Boundaries

- Wolverine 6.51 is isolated to `Shared.EventBus.Wolverine`. It consumes with in-process retries and native quarantine; exhausted handler failures get signed terminal custody through a confirmed publication before acknowledgment. The adapter provides `WolverineTerminalMessageReader`, protected replay transport and a replay custody verifier. It does not provide broker delayed redelivery. Operator composition must supply terminal lookup, authorization, audit and the destination service's shared-database replay ledger. A replay authorizes one consumer attempt; failure needs fresh authorization and an ID. Never upgrade unsigned quarantine or bypass origin expiry/key revocation. See `docs/messaging.md` for composition and recovery semantics.
- Handler failures that clear without an operator (a tenant cache not yet converged, a lock another replica holds) implement `ITenantWrightTransientMessageFailure`. Wolverine retries them inline on the longer `TransientRetryCooldowns` schedule (1/5/15/30/60 s), because it has no message store for `ScheduleRetry`. MassTransit's delayed redelivery already covers them. Never mark security, validation, or permanent failures.
- MassTransit 9.2 is isolated to `Shared.EventBus.MassTransit` and requires `MT_LICENSE` or `MT_LICENSE_PATH`.
- Provider adapters do not own trust, tenant restoration, durable inbox/outbox state, or contract policy.

## Durable Neutral Persistence

`Shared.EntityFrameworkCore/Messaging` owns `TenantWrightOutboxDispatcher`, `TenantWrightOutboxDispatchSignal`, `TenantWrightOutboxMessageSender`, `TenantWrightInboxTransaction`, `TenantWrightInboxRetentionService`, and `TenantWrightMessagingStore`. Register it through `AddTenantWrightMessagingPersistence<TDbContext>` and map it with `modelBuilder.AddTenantWrightMessagingEntities()`.

The dispatcher and retention service fan out across configured tenant databases. Publish state changes through the owning UoW so durable messages cannot escape before commit. Keep handlers idempotent because delivery can repeat.

## Trust and Failure Rules

- Preserve canonical serialization, message identity, tenant scope, correlation, signed attestations, key IDs, and revocation.
- Keep private key paths and public-key registries in `Messaging:Trust`; never log key material or paths on validation failure.
- Bound prefetch, concurrency, immediate retry, delayed redelivery where supported, retention, replay authorization, and shutdown.
- A wake signal may reduce latency but cannot replace durable polling.
- Cleanup must not remove pending, leased, parked, or ambiguous records.
- Diagnose connection, TLS, authentication, topology, trust, handler, retry, and persistence failures separately.

## Evidence

Run focused `Shared.EventBus.Tests`, `Shared.EntityFrameworkCore.Tests`, and publisher/consumer tests. Use `scripts/verify-eto-placement.ps1` for contract placement. Claims about broker routing, acknowledgement, retry, or dead-letter behavior require a real broker check with prior approval to start infrastructure.
