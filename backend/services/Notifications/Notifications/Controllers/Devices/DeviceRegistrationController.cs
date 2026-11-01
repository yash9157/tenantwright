using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Notifications.Application.Contracts.Devices;
using Shared.AuditLogs.Auditing;

namespace Notifications.Controllers.Devices;

/// <summary>Self-service push device-token registration for the current authenticated user.</summary>
[ApiController]
[Route("api/notifications/devices")]
[Authorize]
public class DeviceRegistrationController : ControllerBase
{
    private readonly IDeviceRegistrationAppService _appService;

    public DeviceRegistrationController(IDeviceRegistrationAppService appService)
    {
        _appService = appService;
    }

    [HttpGet]
    public Task<List<NotificationDeviceTokenDto>> GetMyDevicesAsync()
    {
        return _appService.GetMyDevicesAsync();
    }

    [HttpPost]
    [Audited]
    public Task<NotificationDeviceTokenDto> RegisterAsync([FromBody] RegisterDeviceTokenDto input)
    {
        return _appService.RegisterAsync(input);
    }

    [HttpDelete("{id:guid}")]
    [Audited]
    public Task UnregisterAsync(Guid id)
    {
        return _appService.UnregisterAsync(id);
    }
}
