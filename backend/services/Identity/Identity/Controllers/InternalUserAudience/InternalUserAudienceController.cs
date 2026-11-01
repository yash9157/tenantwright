using Identity.Application.Contracts.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shared.AuditLogs.Auditing;

namespace Identity.Controllers.InternalUserAudience;

[ApiController]
[IntegrationService]
[Route("api/identity/internal/user-audience")]
[Authorize(Policy = "IdentityInternal")]
public class InternalUserAudienceController : ControllerBase
{
    private readonly IInternalUserAudienceLookupAppService _lookupAppService;

    public InternalUserAudienceController(IInternalUserAudienceLookupAppService lookupAppService)
    {
        _lookupAppService = lookupAppService;
    }

    [HttpGet("roles/{roleName}/users")]
    public Task<UserAudiencePageDto> GetUsersByRoleAsync(
        string roleName,
        [FromQuery] int skipCount = 0,
        [FromQuery] int maxResultCount = 200)
    {
        return _lookupAppService.GetUsersByRoleAsync(roleName, skipCount, maxResultCount);
    }

    [HttpGet("tenant-users")]
    public Task<UserAudiencePageDto> GetTenantUsersAsync(
        [FromQuery] int skipCount = 0,
        [FromQuery] int maxResultCount = 200)
    {
        return _lookupAppService.GetTenantUserIdsAsync(skipCount, maxResultCount);
    }
}
