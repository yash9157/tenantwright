# Verification

Run commands from `backend/` and select the smallest projects proving the changed boundary.

```powershell
dotnet test tests/Permissions.Tests/Permissions.Tests.csproj
dotnet test tests/Shared.Permissions.Tests/Shared.Permissions.Tests.csproj
dotnet test tests/Identity.Tests/Identity.Tests.csproj
dotnet build TenantWright.slnx
```

Use filters while iterating, then run the affected project without a filter before completion. Add service tests when a service-owned definition or policy changes.

## Required Matrix

Cover as applicable:

- exact valid permission and unknown permission;
- allow, deny, parent/wildcard, and expiry precedence;
- user, immutable role, client, tenant, and host provider identity;
- wrong tenant, ambient tenant on host path, malformed ID, and inactive role;
- feature enabled, disabled, stale, and unavailable;
- approval, self-approval rejection, conflict, cancellation, and concurrency;
- delegation published/unpublished, allowed/disallowed target, scope, effect, and expiry;
- cold/warm cache, invalidation, replay, stale version, and cache outage;
- endpoint policy plus contextual application authorization.

The runtime smoke script `scripts/run-auth-token-permission-smoke.ps1` requires running services and explicit user approval. Run `scripts/run-quality-gates.ps1` only when the user says "Run Quality Gates". Record skipped runtime evidence and remaining risk.
