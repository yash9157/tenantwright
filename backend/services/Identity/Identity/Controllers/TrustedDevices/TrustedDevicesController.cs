using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Identity.Application.Contracts.TrustedDevices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Validation.AspNetCore;
using Shared.AuditLogs.Auditing;

namespace Identity.Controllers.TrustedDevices;

[ApiController]
[Route("api/identity/profile/trusted-devices")]
[Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)]
public sealed class TrustedDevicesController : ControllerBase
{
    private readonly ITrustedDeviceAppService _trustedDeviceAppService;

    public TrustedDevicesController(ITrustedDeviceAppService trustedDeviceAppService)
    {
        _trustedDeviceAppService = trustedDeviceAppService;
    }

    [HttpGet]
    public Task<List<TrustedDeviceDto>> GetMyTrustedDevicesAsync([FromQuery] bool includeExpired = false)
    {
        return _trustedDeviceAppService.GetMyTrustedDevicesAsync(includeExpired);
    }

    [HttpDelete("{id:guid}")]
    [Audited(Category = "auth.session")]
    public Task RevokeMyTrustedDeviceAsync([FromRoute] Guid id)
    {
        return _trustedDeviceAppService.RevokeMyTrustedDeviceAsync(id);
    }

    [HttpPost("revoke-all")]
    [Audited(Category = "auth.session")]
    public Task<int> RevokeAllMyTrustedDevicesAsync()
    {
        return _trustedDeviceAppService.RevokeAllMyTrustedDevicesAsync();
    }
}
