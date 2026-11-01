# EF Core Migration Workflow

Run commands from `backend/` and use paths discovered from source.

## Discover the Owner

```powershell
rg --files services/<Service> app/Auth | rg "DbContext.*Factory\.cs$|EntityFrameworkCore\.csproj$|[\\/]Program\.cs$"
dotnet ef dbcontext list --project <ef-project> --startup-project <host-project>
dotnet ef migrations list --project <ef-project> --startup-project <host-project>
```

A service normally keeps migrations in its EntityFrameworkCore project and uses its host as startup. Auth may use `app/Auth/Auth` for both roles. Verify the actual design-time factory and environment handling.

## Check Lineage First

Compare source migration IDs with every persistent target in scope:

```sql
SELECT "MigrationId"
FROM "__EFMigrationsHistory"
ORDER BY "MigrationId";
```

Use approved secret injection and query only identifiers. Stop if a target contains an ID absent from source. Also determine whether each migration exists in a released artifact.

The repository lineage checker currently lives at:

```powershell
python tools/deployment/verify_migration_lineage.py --help
```

## Generate and Review

```powershell
dotnet ef migrations add <IntentName> --project <ef-project> --startup-project <host-project> --context <DbContextType>
dotnet ef migrations has-pending-model-changes --project <ef-project> --startup-project <host-project> --context <DbContextType>
dotnet ef migrations script --project <ef-project> --startup-project <host-project> --context <DbContextType>
```

Review the migration, designer, and model snapshot together. Confirm operations, Npgsql types, defaults, value generation, tenant-qualified uniqueness, foreign-key actions, messaging persistence tables, indexes, and absence of unrelated churn.

## Lineage Safety

- Append when the current lineage is coherent and the change is a real next state.
- Remove or regenerate only a newly created, unshipped, unapplied latest migration.
- Never edit, delete, rename, reorder, or regenerate an applied or released migration.
- Never conceal divergence with conditional DDL or manual `__EFMigrationsHistory` edits.
- Do not run `database update` against an unidentified or shared target.

A persistent reset is destructive. It requires explicit approval for the exact environment/database, verification of source and applied IDs, writer isolation, application of the full replacement chain, and post-reset ledger/schema/health checks. `scripts/reset-local-databases.ps1` does not remove this approval requirement.

## Verification

Run focused model and migration tests, apply the full chain to an empty PostgreSQL database when schema correctness changes, and test upgrade from the previous state when rollout behavior matters. Build the owning project and update the relevant flat documentation under `docs/`.
