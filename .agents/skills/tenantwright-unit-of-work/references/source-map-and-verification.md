# UoW Source Map and Verification

## Stable Owners

```text
backend/shared/Shared.Uow/**
backend/shared/Shared.EntityFrameworkCore/IDbContextProvider.cs
backend/shared/Shared.EntityFrameworkCore/Uow/EntityFrameworkCore/**
backend/shared/Shared.EntityFrameworkCore/Repositories/**
backend/shared/Shared.EntityFrameworkCore/Messaging/**
backend/shared/Shared.AspNetCore.Host/**
backend/tests/Shared.Modularity.Tests/**
backend/tests/Shared.EntityFrameworkCore.Tests/**
```

Service and app code provide intercepted application paths, request handlers, message handlers, jobs, workflow activities, or explicit executor callers. Verify registration and invocation rather than inferring interception from a class name.

## Discovery

Run from `backend/`:

```powershell
rg -n "UnitOfWorkAttribute|IUnitOfWorkExecutor|IUnitOfWorkManager|IDbContextProvider|UnitOfWorkInterceptor" shared services app tests -g "*.cs"
rg -n "ExecuteTransactionalAsync|ExecuteNonTransactionalAsync|GetDbContextAsync|\.Begin\(" shared services app tests -g "*.cs"
rg -n "Rollback|Transaction|Savepoint|RequiresNew|OperationCanceledException" tests shared services app -g "*.cs"
```

Read current interfaces and implementations; do not copy remembered overloads or defaults.

## Evidence by Boundary

- **HTTP:** request-policy precedence, safe versus mutating defaults, disabled/overridden metadata, context acquisition, save-before-result behavior, handled late failures, rollback/disposal failure preservation, and cancellation.
- **Intercepted application method:** proof interception runs, one ambient owner, nested behavior, context reuse, commit/rollback, deferred events, and cancellation.
- **Executor caller:** scope begins before context acquisition, correct transactional choice, expected transaction count, nested/independent semantics, and cleanup after failure.
- **Message consumer:** trust verification and tenant restoration precede handler invocation; the handler uses the consuming UoW; inbox/outbox state and business changes commit or roll back together.
- **Job/worker/workflow activity:** one bounded scope per atomic database unit, external I/O outside transactions, cancellation propagation, retry idempotency, and fresh scopes between batches.

## Focused Commands

```powershell
dotnet test tests/Shared.Modularity.Tests/Shared.Modularity.Tests.csproj --filter "FullyQualifiedName~<FocusedName>"
dotnet test tests/Shared.EntityFrameworkCore.Tests/Shared.EntityFrameworkCore.Tests.csproj --filter "FullyQualifiedName~<FocusedName>"
dotnet test tests/<Service>.Tests/<Service>.Tests.csproj --filter "FullyQualifiedName~<FocusedName>"
```

Build the affected projects. Broker acknowledgement or redelivery claims require approved broker-backed evidence; UoW atomicity itself should be proven with the real relational composition.
