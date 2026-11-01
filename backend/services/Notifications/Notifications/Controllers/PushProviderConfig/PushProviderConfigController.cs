using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Notifications.Application.Contracts.Channels;
using Shared.Permissions.Definitions.Notifications;
using Shared.AuditLogs.Auditing;
using Shared.Uow.Attributes;

namespace Notifications.Controllers.PushProviderConfig;

[ApiController]
[Route("api/notifications/push-config")]
[Authorize]
public class PushProviderConfigController : ControllerBase
{
    private readonly IPushProviderConfigAppService _appService;

    public PushProviderConfigController(IPushProviderConfigAppService appService)
    {
        _appService = appService;
    }

    [HttpGet]
    [Authorize(Policy = NotificationsPermissions.Config.Push)]
    public Task<List<PushProviderConfigDto>> GetListAsync([FromQuery] Guid? tenantId)
    {
        return _appService.GetListAsync(tenantId);
    }

    [HttpGet("current-tenant")]
    [Authorize(Policy = NotificationsPermissions.Config.Push)]
    public Task<List<PushProviderConfigDto>> GetCurrentTenantListAsync()
    {
        return _appService.GetCurrentTenantListAsync();
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = NotificationsPermissions.Config.Push)]
    public Task<PushProviderConfigDto> GetAsync(Guid id)
    {
        return _appService.GetAsync(id);
    }

    [HttpPost]
    [Audited]
    [Authorize(Policy = NotificationsPermissions.Config.Push)]
    public Task<PushProviderConfigDto> CreateAsync([FromBody] CreatePushProviderConfigDto input)
    {
        return _appService.CreateAsync(input);
    }

    [HttpPost("current-tenant")]
    [Audited]
    [Authorize(Policy = NotificationsPermissions.Config.Push)]
    public Task<PushProviderConfigDto> CreateForCurrentTenantAsync([FromBody] CreatePushProviderConfigDto input)
    {
        return _appService.CreateForCurrentTenantAsync(input);
    }

    [HttpPut("{id:guid}")]
    [Audited]
    [Authorize(Policy = NotificationsPermissions.Config.Push)]
    public Task<PushProviderConfigDto> UpdateAsync(Guid id, [FromBody] UpdatePushProviderConfigDto input)
    {
        return _appService.UpdateAsync(id, input);
    }

    [HttpDelete("{id:guid}")]
    [Audited]
    [Authorize(Policy = NotificationsPermissions.Config.Push)]
    public Task DeleteAsync(Guid id)
    {
        return _appService.DeleteAsync(id);
    }

    [HttpPost("{id:guid}/set-default")]
    [Audited]
    [Authorize(Policy = NotificationsPermissions.Config.Push)]
    public Task<PushProviderConfigDto> SetDefaultAsync(Guid id)
    {
        return _appService.SetDefaultAsync(id);
    }

    [HttpPost("{id:guid}/test-send")]
    [UnitOfWork(isTransactional: false)]
    [Audited]
    [Authorize(Policy = NotificationsPermissions.Config.TestSend)]
    public Task<PushProviderConfigTestSendResultDto> TestSendAsync(Guid id, [FromBody] PushProviderConfigTestSendInput input)
    {
        return _appService.SendTestAsync(id, input);
    }
}
