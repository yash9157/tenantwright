---
name: tenantwright-api-event-contracts
description: Backend contract workflow for HTTP requests and responses, Application.Contracts, OpenAPI, distributed *Eto messages, canonical JSON, route ownership, and publisher-consumer agreement.
---
# API and Event Contracts

Own wire meaning and contract placement; route runtime behavior to its specialist.

## Inspect First

1. Read root and backend governance, then identify the semantic owner and every publisher, consumer, or HTTP caller.
2. Use [contract source map](references/contract-map.md), then [API rules](references/api-contract-rules.md) or [event rules](references/event-contract-rules.md).
3. Apply [contract verification](references/contract-verification.md).
4. For request shape and FluentValidation, read [request validation](references/request-validation.md).

## Required Workflow

1. Inspect the existing type, serializer, route, authorization, tenant scope, tests, and documentation.
2. Design the smallest complete contract for the required behavior.
3. This template has no external consumer today: replace weak internal shapes and migrate every in-repository caller together. Add compatibility only for an identified real consumer and removal condition.
4. Keep request validation, authorization, tenancy, pagination, serialization, and error behavior explicit.
5. Align OpenAPI and tests. No generated client exists today; if one is introduced later, generate it from the reviewed OpenAPI rather than hand-writing a duplicate contract.

## Ownership

- HTTP DTOs and public application interfaces live in the owning `*.Application.Contracts` project.
- Cross-service `*Eto` wire types live in `Shared.EventContracts`; add each serializable type to `TenantWrightContractJsonContext`.
- `TenantWrightMessagingTopology` owns route kind, destination, producer principal, endpoint owner, and producer allow-list.
- `Shared.EventBus` owns transport-neutral runtime behavior; provider adapters do not own contracts.
- Domain entities, EF types, transport implementation types, and host models do not cross the wire.

## Definition of Done

Owner and consumers agree on one wire meaning; security, tenant scope, validation, serialization, and failure semantics are tested; obsolete shapes are removed; `scripts/verify-eto-placement.ps1` passes when `*Eto` placement changes; and affected flat docs such as `docs/architecture.md`, `docs/messaging.md`, or `docs/security.md` are current.
