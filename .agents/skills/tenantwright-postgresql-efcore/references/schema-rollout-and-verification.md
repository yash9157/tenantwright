# Schema Rollout and Verification

## Evidence Matrix

| Claim | Minimum evidence |
| --- | --- |
| EF model contains mapping, constraint, index, or filter | Owning DbContext model test |
| Migration represents the intended delta | Migration, designer, and snapshot review |
| Full chain creates the schema | Empty PostgreSQL migration application |
| Upgrade preserves required behavior | Previous-state to current-state migration test |
| Constraint rejects invalid state | Relational test against a capable provider |
| PostgreSQL SQL, lock, type, or planner behavior works | PostgreSQL/Testcontainers test |
| Transaction rolls back | Real UoW plus relational DbContext composition |
| Tenant isolation holds | Positive and negative tenant tests, including bypass paths |

## Focused Commands

Run from `backend/`:

```powershell
dotnet test tests/<Service>.Tests/<Service>.Tests.csproj --filter "FullyQualifiedName~<FocusedName>"
dotnet test tests/Shared.EntityFrameworkCore.Tests/Shared.EntityFrameworkCore.Tests.csproj --filter "FullyQualifiedName~<FocusedName>"
dotnet build <affected-project>.csproj
```

Use `dotnet build TenantWright.slnx` or `dotnet test TenantWright.slnx` only when the change scope justifies solution-wide evidence.

## Completion Checklist

- source and applied migration ledgers were compared where lineage changed;
- applied/released migrations were not modified;
- migration, designer, and snapshot agree;
- tenant filters, keys, joins, and uniqueness remain fail closed;
- provider-specific behavior was tested with PostgreSQL;
- cancellation and concurrency paths were covered where relevant;
- affected flat documentation under `docs/` was updated or explicitly found unaffected;
- skipped live or destructive checks and remaining rollout risk were reported.
