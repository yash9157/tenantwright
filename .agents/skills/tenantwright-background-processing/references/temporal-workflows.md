# Temporal Workflows

Temporal is confined to `services/Workflows`. The application project contains the current workflow, worker, client provider, starter, options, and message handler. The `Sagas` area has no implementation. Verify current names and package pins from source before editing.

```powershell
Get-ChildItem services/Workflows -Recurse -File | Where-Object Name -Match 'Temporal|Workflow'
git grep -n -E "Temporalio|WorkflowRun|WorkflowSignal|ExecuteActivityAsync|StartWorkflowAsync|AddWorkflow" -- services/Workflows tests/Workflows.Tests
```

## Determinism

Workflow code must replay deterministically. Use Temporal workflow clocks, timers, waits, and supported deterministic APIs. Do not perform database, HTTP, filesystem, ambient request, thread-pool, or wall-clock work inside workflow code.

Activities own external effects. Propagate activity cancellation and use bounded UoW scopes only around database work. A long-running workflow never holds a DbContext or transaction.

## Identity and Recovery

- Define workflow ID, start conflict behavior, signal authorization, and terminal-state rules.
- Distinguish duplicate start, missing run, late signal, failed run, terminated run, and completed run.
- Bound client RPCs, activity execution, retries, and worker shutdown.
- Preserve revision ordering when signals and timers race.
- Activity retries can repeat effects; use stable operation identity and unknown-outcome recovery.
- Treat workflow inputs, results, history, and search attributes as persisted data; keep them minimal and bounded.
- Before changing code used by retained histories, design replay and worker-version behavior. Persistent histories are not implicitly disposable.

## Verification

Use state tests for transition rules, activity tests for effects and DI, and server or replay tests for SDK guarantees. Cover duplicate start, stale or terminal signals, timer races, restart, cancellation of each RPC, ambiguous effects, tenant mismatch, and retained-history replay where relevant. Do not claim server behavior unless a Temporal server actually ran with approval.
