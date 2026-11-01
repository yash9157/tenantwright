using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Notifications.Application.Contracts.Channels;
using Shared.Permissions.Definitions.Notifications;
using Shared.AuditLogs.Auditing;
using Shared.Uow.Attributes;

namespace Notifications.Controllers.SmsProviderConfig;

[ApiController]
[Route("api/notifications/sms-config")]
[Authorize]
public class SmsProviderConfigController : ControllerBase
{
    private readonly ISmsProviderConfigAppService _appService;

    public SmsProviderConfigController(ISmsProviderConfigAppService appService)
    {
        _appService = appService;
    }

    [HttpGet]
    [Authorize(Policy = NotificationsPermissions.Config.Sms)]
    public Task<List<SmsProviderConfigDto>> GetListAsync([FromQuery] Guid? tenantId)
    {
        return _appService.GetListAsync(tenantId);
    }

    [HttpGet("current-tenant")]
    [Authorize(Policy = NotificationsPermissions.Config.Sms)]
    public Task<List<SmsProviderConfigDto>> GetCurrentTenantListAsync()
    {
        return _appService.GetCurrentTenantListAsync();
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = NotificationsPermissions.Config.Sms)]
    public Task<SmsProviderConfigDto> GetAsync(Guid id)
    {
        return _appService.GetAsync(id);
    }

    [HttpPost]
    [Audited]
    [Authorize(Policy = NotificationsPermissions.Config.Sms)]
    public Task<SmsProviderConfigDto> CreateAsync([FromBody] CreateSmsProviderConfigDto input)
    {
        return _appService.CreateAsync(input);
    }

    [HttpPost("current-tenant")]
    [Audited]
    [Authorize(Policy = NotificationsPermissions.Config.Sms)]
    public Task<SmsProviderConfigDto> CreateForCurrentTenantAsync([FromBody] CreateSmsProviderConfigDto input)
    {
        return _appService.CreateForCurrentTenantAsync(input);
    }

    [HttpPut("{id:guid}")]
    [Audited]
    [Authorize(Policy = NotificationsPermissions.Config.Sms)]
    public Task<SmsProviderConfigDto> UpdateAsync(Guid id, [FromBody] UpdateSmsProviderConfigDto input)
    {
        return _appService.UpdateAsync(id, input);
    }

    [HttpDelete("{id:guid}")]
    [Audited]
    [Authorize(Policy = NotificationsPermissions.Config.Sms)]
    public Task DeleteAsync(Guid id)
    {
        return _appService.DeleteAsync(id);
    }

    [HttpPost("{id:guid}/set-default")]
    [Audited]
    [Authorize(Policy = NotificationsPermissions.Config.Sms)]
    public Task<SmsProviderConfigDto> SetDefaultAsync(Guid id)
    {
        return _appService.SetDefaultAsync(id);
    }

    [HttpPost("{id:guid}/test-send")]
    [UnitOfWork(isTransactional: false)]
    [Audited]
    [Authorize(Policy = NotificationsPermissions.Config.TestSend)]
    public Task<SmsProviderConfigTestSendResultDto> TestSendAsync(Guid id, [FromBody] SmsProviderConfigTestSendInput input)
    {
        return _appService.SendTestAsync(id, input);
    }
}
