# Focused Coverage and Verification

## Find the Owner

From `backend/`:

```powershell
Get-ChildItem tests -Directory
rg -n "TargetTypeOrMethod|PolicyName|ErrorCode" tests -g "*.cs"
dotnet sln TenantWright.slnx list
```

Read the production path and existing neighboring tests before selecting a fixture or test double.

## Commands

```powershell
dotnet test tests/<Project>/<Project>.csproj --filter "FullyQualifiedName~FocusedName"
dotnet test tests/<Project>/<Project>.csproj
dotnet build TenantWright.slnx
```

Run the focused test during iteration, then the full affected project. Run solution build for changed contracts, registration, project references, generators, or public APIs. Run `dotnet test TenantWright.slnx` when broad shared behavior warrants it.

## Coverage Matrix

Select applicable rows:

- success and expected denial/failure;
- null, empty, malformed, boundary, and oversized input;
- unauthorized, wrong tenant, wrong provider, and missing context;
- duplicate, retry, cancellation, timeout, and partial failure;
- optimistic concurrency and simultaneous actors;
- transaction commit, rollback, event/outbox behavior, and replay;
- cold/warm cache, invalidation, unavailable cache, and stale state;
- provider-accurate query, constraint, migration, or locking behavior;
- startup failure for invalid security-critical configuration.

Coverage percentage is supporting evidence, not proof. Prefer branch and failure-mode evidence over tests written only to increase a number.

## Reporting

Report exact commands and outcomes, skipped container or runtime checks, and residual risk. Do not claim broader verification than fresh output proves. Update `docs/testing.md` only when repository-wide guidance or ownership changes.
