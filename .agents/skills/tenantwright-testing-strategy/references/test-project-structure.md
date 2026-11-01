# Test Project Structure

`backend/tests` contains 27 xUnit projects:

```text
AuditLogs.Tests
Auth.Tests
Gateway.Tests
TenantWright.AppHost.Tests
Identity.Tests
Notifications.Tests
Permissions.Tests
RepoIndexMcp.Tests
Shared.AuditLogs.Tests
Shared.Autofac.Tests
Shared.BackgroundTasks.Tests
Shared.BackgroundTasks.Hangfire.Tests
Shared.BlobStoring.Tests
Shared.CurrentUser.Tests
Shared.Data.Tests
Shared.Domain.Tests
Shared.EntityFrameworkCore.Tests
Shared.EventBus.Tests
Shared.EventBus.Wolverine.Tests
Shared.EventContracts.Tests
Shared.Modularity.Tests
Shared.MultiTenancy.Tests
Shared.Permissions.Tests
Shared.Security.Tests
Shared.Valkey.IntegrationTests
Tenant.Tests
Workflows.Tests
```

## Ownership

- Service projects test service domain, application, persistence, and HTTP behavior.
- `Shared.*.Tests` test reusable infrastructure at its owning boundary.
- `Gateway.Tests` owns YARP route and gateway policy behavior.
- `TenantWright.AppHost.Tests` owns static orchestration configuration checks.
- `RepoIndexMcp.Tests` owns optional local repository-index tooling behavior; generated output is not tracked authority.

Keep tests in the project owning the behavior. Add a new project only when a real ownership boundary lacks a suitable test host, then add it to `TenantWright.slnx`.

## Commands

From `backend/`:

```powershell
dotnet test tests/Permissions.Tests/Permissions.Tests.csproj
dotnet test TenantWright.slnx
dotnet build TenantWright.slnx
```

Use the exact project path returned by source discovery. Do not create alternate test trees or duplicate shared fixtures without an ownership reason.
