using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Permissions.Application.Contracts.Authorization;
using Permissions.Application.Contracts.Provisioning;
using Shared.Data.Seeding;
using Shared.AuditLogs.Auditing;

namespace Permissions.Controllers.DevProvision;

[ApiController]
[Route("api/permissions/dev/provision")]
[Authorize(Policy = PermissionsPolicies.CheckFeaturesInternal)]
public class DevProvisionController : ControllerBase
{
    private readonly IDataSeeder _dataSeeder;
    private readonly IWebHostEnvironment _environment;

    public DevProvisionController(IDataSeeder dataSeeder, IWebHostEnvironment environment)
    {
        _dataSeeder = dataSeeder;
        _environment = environment;
    }

    [HttpPost("default-admin-grants")]
    public async Task<IActionResult> EnsureDefaultAdminGrantsAsync(
        [FromBody] DefaultAdminGrantsProvisionInput? input,
        CancellationToken cancellationToken)
    {
        if (!_environment.IsDevelopment())
        {
            return NotFound();
        }

        cancellationToken.ThrowIfCancellationRequested();
        await _dataSeeder.SeedAsync(new DataSeedContext(input?.TenantId));
        return Ok();
    }
}
