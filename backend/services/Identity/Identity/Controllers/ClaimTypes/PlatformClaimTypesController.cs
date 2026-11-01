using Identity.Application.Contracts.ClaimTypes;
using Identity.Application.Contracts.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Validation.AspNetCore;
using Shared.AuditLogs.Auditing;
using Shared.Permissions.Definitions.Identity;

namespace Identity.Controllers.ClaimTypes;

[ApiController]
[Route("api/identity/platform/tenants/{tenantId:guid}/claim-types")]
[Authorize(
    AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme,
    Policy = IdentityPermissions.ClaimTypes.Default)]
public sealed class PlatformClaimTypesController : ControllerBase
{
    private readonly IIdentityClaimTypeAppService _claimTypes;

    public PlatformClaimTypesController(IIdentityClaimTypeAppService claimTypes)
    {
        _claimTypes = claimTypes;
    }

    [HttpGet]
    public Task<PagedResultDto<ClaimTypeDto>> GetListAsync(
        Guid tenantId,
        [FromQuery] ClaimTypeListInput input)
        => _claimTypes.GetPlatformListAsync(tenantId, input);

    [HttpGet("{id:guid}")]
    public Task<ClaimTypeDto> GetAsync(Guid tenantId, Guid id)
        => _claimTypes.GetPlatformAsync(tenantId, id);

    [HttpPost]
    [Authorize(Policy = IdentityPermissions.ClaimTypes.Create)]
    [Audited(Category = "identity.claim-types")]
    public Task<ClaimTypeDto> CreateAsync(Guid tenantId, [FromBody] ClaimTypeCreateDto input)
        => _claimTypes.CreatePlatformAsync(tenantId, input);

    [HttpPut("{id:guid}")]
    [Authorize(Policy = IdentityPermissions.ClaimTypes.Update)]
    [Audited(Category = "identity.claim-types")]
    public Task<ClaimTypeDto> UpdateAsync(Guid tenantId, Guid id, [FromBody] ClaimTypeUpdateDto input)
        => _claimTypes.UpdatePlatformAsync(tenantId, id, input);

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = IdentityPermissions.ClaimTypes.Delete)]
    [Audited(Category = "identity.claim-types")]
    public Task DeleteAsync(Guid tenantId, Guid id)
        => _claimTypes.DeletePlatformAsync(tenantId, id);
}
