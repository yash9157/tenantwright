using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Permissions.Application.Contracts.Authorization;
using Permissions.Application.Contracts.TenantPermissionAccess;

namespace Permissions.Controllers.TenantPermissionAccess;

[ApiController]
[Route("api/permissions/tenant-delegation")]
[Authorize(Policy = PermissionsPolicies.ManageTenantAccess)]
public sealed class TenantDelegationController(ITenantDelegationAppService service) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = PermissionsPolicies.ReadTenantAccess)]
    public Task<IReadOnlyList<TenantDelegationEntryDto>> GetAsync(
        [FromQuery] TenantDelegationListInput input,
        CancellationToken cancellationToken) => service.GetAsync(input, cancellationToken);

    [HttpGet("options")]
    [Authorize(Policy = PermissionsPolicies.ReadTenantAccess)]
    public Task<IReadOnlyList<TenantDelegationOptionDto>> GetOptionsAsync(
        CancellationToken cancellationToken) => service.GetOptionsAsync(cancellationToken);

    [HttpPut]
    public Task<TenantDelegationMutationResult> MutateAsync(
        [FromBody] TenantDelegationMutationInput input,
        CancellationToken cancellationToken) => service.MutateAsync(input, cancellationToken);
}
