# Runtime Verification

Discover the nearest test owner from project references and symbol use:

```powershell
git grep -n "<ChangedType>" -- tests
dotnet test tests/<Project>/<Project>.csproj --nologo --filter "FullyQualifiedName~<FocusedName>"
```

| Claim | Minimum evidence |
|---|---|
| DI or module registration | owner test plus one consuming host composition |
| startup option failure | binding/validator test; host startup when pipeline behavior matters |
| Valkey cache or lock atomicity | real Valkey integration; fakes only prove caller behavior |
| message publish, consume, retry, or idempotency | neutral core, provider adapter, publisher, and consumer tests; broker evidence for transport claims |
| tenant or current-user context | positive and negative isolation through the actual boundary |
| HTTP client behavior | composed handler test plus target policy review |
| health endpoint | registration/state tests plus running endpoint for runtime claims |
| logs, metrics, or traces | emitter test and exporter evidence when end-to-end visibility is claimed |

Run checks from `backend`. Typical commands are `dotnet build TenantWright.slnx`, `dotnet test TenantWright.slnx`, or a focused test project. Do not start AppHost, containers, or a service without approval. Report exact checks, skipped runtime evidence, documentation updates, and remaining risk.
