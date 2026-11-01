# Contract Verification

## Select Evidence

| Change | Minimum focused evidence |
|---|---|
| HTTP request or response DTO | owning contract and controller tests, OpenAPI shape, caller review |
| route, verb, status, or security metadata | controller metadata or OpenAPI evidence plus caller review |
| distributed `*Eto` shape | canonical serialization, topology, publisher, and handler tests |
| nullability or enum change | serialization fixture and every affected consumer |
| shared contract surface | owner test plus each real cross-boundary consumer |

## Commands

Run from `backend`:

```powershell
git grep -n -E "ContractTests|Eto|TenantWrightContractJsonContext|TenantWrightMessagingTopology|OpenApi|Swagger" -- tests app services shared
dotnet test tests/<Project>/<Project>.csproj
powershell -ExecutionPolicy Bypass -File scripts/verify-eto-placement.ps1
```

Run the placement verifier when `*Eto` declarations move or are added. Run the matching service test project for changed controllers, publishers, or handlers. Inspect both sides of every message contract and verify exact serializer metadata.

No generated client or request collection is tracked today. Do not claim synchronization with artifacts that do not exist. If a generated consumer is added later, include its regeneration and compile checks in the same contract change.

## Completion Checks

- no entity, EF, host, or provider type leaked onto the wire;
- no omitted publisher or consumer;
- no silent route, wire-name, enum, nullability, or tenant-scope drift;
- no compatibility layer without an identified consumer;
- OpenAPI, tests, and flat canonical docs agree with source;
- skipped runtime or external-consumer evidence is reported explicitly.
