using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shared.AuditLogs.Auditing;
using Shared.Permissions.Definitions.Tenant;
using Tenant.Application.Contracts.Tenants;

namespace Tenant.Controllers.Tenants;

/// <summary>
/// Host management of a tenant's dedicated service databases, one route per catalog name (for example
/// <c>/databases/Notifications</c>). Tenants share each service's database unless a host administrator assigns a
/// dedicated one here. No route ever returns a connection string, and no assignment moves data.
/// </summary>
[ApiController]
[Route("api/tenant/tenants/{id:guid}/databases")]
[Authorize(Policy = TenantPermissions.Management.Default)]
public class TenantDatabasesController : ControllerBase
{
    private readonly ITenantDatabaseAppService _databases;

    public TenantDatabasesController(ITenantDatabaseAppService databases)
    {
        _databases = databases;
    }

    [HttpGet]
    [Authorize(Policy = TenantPermissions.ConnectionStrings.Read)]
    public Task<TenantDatabaseListDto> GetDatabasesAsync(Guid id) => _databases.GetDatabasesAsync(id);

    [HttpPost("{name}/test")]
    [Authorize(Policy = TenantPermissions.ConnectionStrings.Test)]
    [Audited]
    public Task<TenantDatabaseTestResultDto> TestDatabaseAsync(Guid id, string name) =>
        _databases.TestDatabaseAsync(id, name);

    /// <summary>
    /// Assigns or re-points the dedicated database. Existing data is never moved; every assignment
    /// requires <c>acknowledgeNoDataMigration: true</c>.
    /// </summary>
    [HttpPut("{name}")]
    [Authorize(Policy = TenantPermissions.ConnectionStrings.Update)]
    [Audited(Category = "tenant.lifecycle")]
    public Task<SetTenantDatabaseResultDto> SetDatabaseAsync(
        Guid id,
        string name,
        [FromBody] SetTenantDatabaseInput input) =>
        _databases.SetDatabaseAsync(id, name, input);

    /// <summary>
    /// Returns the tenant to the shared database. Existing data is never moved; requires
    /// <c>acknowledgeNoDataMigration=true</c>.
    /// </summary>
    [HttpDelete("{name}")]
    [Authorize(Policy = TenantPermissions.ConnectionStrings.Delete)]
    [Audited(Category = "tenant.lifecycle")]
    public Task<TenantDatabaseStatusDto> RemoveDatabaseAsync(
        Guid id,
        string name,
        [FromQuery] RemoveTenantDatabaseInput input) =>
        _databases.RemoveDatabaseAsync(id, name, input);
}
