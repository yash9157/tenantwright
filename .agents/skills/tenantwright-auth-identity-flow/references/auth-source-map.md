# Auth and Identity Source Map

Use source, configuration, and tests as truth.

## Stable Owners

```text
app/Auth/Auth/                         OpenIddict server, Quartz cleanup, Razor auth pages, token issuance
services/Identity/                     users, roles, sessions, MFA/passkeys, trusted devices, invitations
services/Identity/                     login risk, security logs, organization units, internal targets
services/Tenant/                       tenant lifecycle and tenant database management API
shared/Shared.Security/                shared security primitives and delegated-authority contracts
shared/Shared.CurrentUser/             current-user abstraction
shared/Shared.MultiTenancy/            tenant context and resolution
shared/Shared.AspNetCore.Host/          session validation and global exception middleware
shared/Shared.Permissions/              policy consumption and internal token providers
tests/Auth.Tests/                       Auth behavior
tests/Identity.Tests/                   Identity behavior
tests/Shared.Security.Tests/            shared security behavior
```

The Auth host uses OpenIddict 7.2 and Quartz. Identity references Fido2 for passkeys. Razor pages are under `app/Auth/Auth/Pages`.

## Discovery

From `backend/`:

```powershell
rg -n "ClaimsPrincipal|SetDestinations|SetScopes|SetResources|RefreshToken|authorization_code|password" app/Auth/Auth -g "*.cs"
rg -n "session_id|tenantid|RoleId|AuthzVersion|AddClaim" app/Auth services/Identity shared -g "*.cs"
rg -n "Mfa|Passkey|Fido2|TrustedDevice|Session|Revoke|Invitation|LoginRisk|SecurityLog" services/Identity app/Auth -g "*.cs"
rg -n "AddPolicy|Authorize|OpenIddict|ClientId|Scope|Audience" app/Auth services/Identity shared -g "*.cs"
```

Trace password/direct issuance, interactive authorization-code issuance, refresh reconstruction, user-info, and token destinations separately until source proves a shared builder.

## Verification

```powershell
dotnet test tests/Auth.Tests/Auth.Tests.csproj
dotnet test tests/Identity.Tests/Identity.Tests.csproj
dotnet test tests/Shared.Security.Tests/Shared.Security.Tests.csproj
```

Use `scripts/run-auth-token-permission-smoke.ps1` only with explicit approval to run required services. Never copy credentials, secrets, ports, or seeded values into this skill. Update `docs/security.md` for material behavior changes.
