# Distributed Event Contract Rules

## Current Ownership

Concrete cross-service `*Eto` contracts are in `shared/Shared.EventContracts`: audit-log, identity, notification, permission, and tenant contract files. Keep that assembly transport-safe and behavior-free. Register every serializable contract in `TenantWrightContractJsonContext` so canonical source-generated JSON is available.

`TenantWrightMessagingTopology` is the single reviewed catalog for command or event kind, destination, endpoint ownership, `tenantwright-<service>` producer principal, and allowed producers. A host may consume only endpoints assigned to its principal.

Do not place distributed `*Eto` declarations in Application or Application.Contracts. Do not reference MassTransit or Wolverine from a contract assembly. The placement verifier allows a small set of legacy or infrastructure locations; that allow-list is not permission to bypass current ownership.

## Shape and Scope

- Use an event for a fact and a command for directed work.
- Keep payloads minimal, bounded, immutable in meaning, and free of secrets.
- Separate host-scoped and tenant-scoped contracts rather than using a nullable tenant identifier to widen scope.
- Preserve exact CLR identity, canonical JSON names, nullability, enum values, data classification, and producer-consumer meaning unless making a deliberate breaking change.
- Moving a file or assembly does not justify changing wire meaning.
- Update the source-generated JSON context and topology in the same change.
- Handlers receive messages only after trust verification and tenant restoration; contract design must not assume provider-specific headers bypass that pipeline.

## Compatibility

There are no external consumers today, so prefer one correct final contract and migrate all repository publishers and handlers atomically. If a real external consumer is later identified, document its compatibility window and removal criteria before adding a bridge.

## Common Defects

- payload semantics changed without migrating consumers;
- type added without JSON-context or topology registration;
- producer or endpoint ownership widened for convenience;
- tenant and host scope conflated;
- provider package types leaked into the contract;
- domain entities or persistence models sent directly.

Run `scripts/verify-eto-placement.ps1`, serialization tests, topology tests, and focused publisher and handler tests.
