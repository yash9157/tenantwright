# Source Map

Use source and tests as truth. Start with the smallest owning boundary.

## Stable Owners

```text
services/*/*.Domain.Shared/Permissions/            service permission constants and definition providers
services/Permissions/Permissions.Domain.Shared/   management permissions and shared enums
services/Permissions/Permissions.Domain/          grants, conflicts, definitions, delegation, lifecycle
services/Permissions/Permissions.Application/     mutation, evaluation, catalog, seeding, access reviews
services/Permissions/Permissions/                 HTTP endpoints and host policy registration
services/Permissions/Permissions.EntityFrameworkCore/ durable permission state
shared/Shared.Permissions.Abstractions/            definitions and authorization abstractions
shared/Shared.Permissions/                         policy provider, checkers, clients, events, caches
services/Identity/                                 users, immutable roles, membership, internal targets
tests/Permissions.Tests/                           service behavior
tests/Shared.Permissions.Tests/                    shared policy and evaluation behavior
```

Auth token and claim changes belong with `tenantwright-auth-identity-flow`. Database mappings and migrations also require `tenantwright-postgresql-efcore`.

## Discovery

From `backend/`:

```powershell
Get-ChildItem services,shared -Recurse -Filter '*Permissions*.cs'
rg -n "AddPermission|AddChild|RequiresFeature|PermissionDefinition" services shared -g "*.cs"
rg -n "Authorize|RequireAuthorization|IPermissionChecker|CheckAsync" services shared app -g "*.cs"
rg -n "PermissionDelegationDefinition|TenantDelegationAppService" services/Permissions -g "*.cs"
rg -n "PermissionGrantChanged|FeatureValuesChanged|Stamp|Snapshot|Cache" services/Permissions shared/Shared.Permissions -g "*.cs"
```

Verify every path before relying on it; projects can move during template evolution.

## Current Examples

Valid examples include `AuditLogs.Read`, `Notifications.Inbox.Read`, `Permissions.Management.Grants`, `Identity.Users.ManagePermissions`, and `Tenant.Management.ConnectionStrings.Read`. Copy constants from source rather than retyping strings in runtime code.

## Documentation

Update the flat `docs/security.md` page when permission semantics, ownership, or operating guidance changes. Do not treat generated local repository output as authority.
