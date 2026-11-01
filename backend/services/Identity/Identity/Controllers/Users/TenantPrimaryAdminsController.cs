using Identity.Application.Contracts.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Validation.AspNetCore;
using Shared.Permissions.Definitions.Tenant;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Identity.Controllers.Users;

[ApiController]
[Route("api/identity/users/primary-admins")]
[Authorize(
    AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme,
    Policy = TenantPermissions.Management.Default)]
public sealed class TenantPrimaryAdminsController : ControllerBase
{
    private readonly IUserAppService _userAppService;

    public TenantPrimaryAdminsController(IUserAppService userAppService)
    {
        _userAppService = userAppService;
    }

    [HttpPost]
    public Task<List<TenantPrimaryAdminDto>> GetAsync([FromBody] TenantPrimaryAdminsInput input)
    {
        return _userAppService.GetPrimaryAdminsAsync(input);
    }
}
