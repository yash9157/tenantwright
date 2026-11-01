# Project Reference Rules

Compute the live recursive graph from `.csproj` files before editing. The following direction is the standard; optional references require a direct API use.

| From | Normal inward references | Never reference |
|---|---|---|
| host | own Application, own EntityFrameworkCore, required host/shared runtime projects | other service implementation projects; own Domain for convenience |
| Application | own Application.Contracts, own Domain, `Shared.Modularity`; own EntityFrameworkCore only for justified EF work | another service's Application, Domain, EntityFrameworkCore, or host |
| Application.Contracts | own Domain.Shared and `Shared.Modularity` when used | own Application, Domain, EntityFrameworkCore, host, or transport providers |
| Domain | own Domain.Shared, `Shared.Domain`, and `Shared.Modularity` when used | Application, Application.Contracts, EntityFrameworkCore, or host |
| Domain.Shared | dependency-light shared abstractions actually used | Application, Domain, EntityFrameworkCore, host, or runtime permission implementation |
| EntityFrameworkCore | own Domain and `Shared.EntityFrameworkCore` | Application, Application.Contracts, host, or another service implementation |

Approved contract-only references between service boundaries must remain dependency-light and must not create a cycle. Cross-service runtime behavior uses HTTP or `Shared.EventContracts`, not direct implementation references.

## Review

```powershell
Get-ChildItem services/<Service> -Recurse -Filter '*.csproj' | ForEach-Object { $_.FullName; Select-String -Path $_.FullName -Pattern 'ProjectReference|PackageReference' }
dotnet build services/<Service>/<Service>/<Service>.csproj
```

Check reverse consumers as well as direct references. Reject cycles, upward dependencies, provider packages outside their owner, and references added only to access one misplaced type.
