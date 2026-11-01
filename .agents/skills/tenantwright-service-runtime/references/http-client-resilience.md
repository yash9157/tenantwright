# HTTP Client Resilience

Use the installed `Microsoft.Extensions.Http.Resilience` through the existing host or client composition owner. Inspect `orchestration/TenantWright.ServiceDefaults/Extensions.cs`, the named or typed client, token handler, target policy, and effective handler order.

```powershell
git grep -n -E "ConfigureHttpClientDefaults|AddStandardResilienceHandler|AddResilienceHandler|RemoveAllResilienceHandlers" -- orchestration app services shared
git grep -n -E "AddHttpClient|SendAsync|PostAsync|PutAsync|PatchAsync|DeleteAsync" -- app services shared
```

## Replay Rules

- Classify actual side effects; an HTTP verb alone does not prove replay safety.
- Disable retries for unsafe operations unless the server has stable request identity, deduplication, replayable bodies, and unknown-outcome recovery.
- Do not hedge writes with side effects.
- Bound per-attempt and total timeout, retry count, backoff, jitter, and downstream concurrency. Include outer job or workflow retries in the total budget.
- Preserve cancellation and do not retry validation or authorization denials.
- Never return an authorization success fallback when a dependency times out or its circuit opens.
- Recreate required token and tenant headers safely for every attempt without logging them.
- Avoid stacked default, custom, and manual retry loops.

Use recording handlers to test exact attempt counts, cancellation, unsafe methods, exhaustion, handler order, and duplicate-pipeline prevention. Live interoperability and network behavior require separate evidence.
