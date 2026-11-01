# Contract Source Map

## HTTP Path

```text
services/<Owner>/<Owner>.Application.Contracts/**  DTOs and public interfaces
services/<Owner>/<Owner>/Controllers/**            HTTP routes and policies
services/<Owner>/<Owner>.Application/**            implementation and validation
tests/<Owner>.Tests/**                             focused evidence
```

Auth is under `app/Auth`; gateway routes and transforms are under `gateway/ApiGateway`. Discover special owners rather than forcing the service path.

## Distributed Message Path

```text
shared/Shared.EventContracts/**                    transport-safe *Eto types and JSON metadata
shared/Shared.EventBus/Messaging/**                catalog, trust, route, and handler contracts
shared/Shared.EventBus.Hosting/**                  provider selection and host composition
shared/Shared.EventBus.MassTransit/**              MassTransit adapter only
shared/Shared.EventBus.Wolverine/**                Wolverine adapter only
services/** and app/**                             publishers and handlers
scripts/verify-eto-placement.ps1                   placement verifier
```

`TenantWrightContractJsonContext` is the source-generated JSON registry. `TenantWrightMessagingTopology` is the reviewed route, producer-principal, endpoint-owner, and producer allow-list catalog.

## Discovery

Run from `backend`:

```powershell
git grep -n -E "\[Route|\[Http(Get|Post|Put|Patch|Delete)|ProducesResponseType|Authorize" -- app services gateway
git grep -n -E "class .*Dto|record .*Dto|interface I.*AppService" -- app services
git grep -n -E "record .*Eto|class .*Eto|ITenantWrightMessageSender|ITenantWrightMessageHandler|TenantWrightMessagingTopology" -- app services shared tests
```

Verify semantic ownership, every real caller, exact route metadata, serializer registration, and tests before changing a wire shape.
