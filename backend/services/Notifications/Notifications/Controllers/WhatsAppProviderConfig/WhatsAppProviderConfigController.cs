using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Notifications.Application.Contracts.Channels;
using Shared.Permissions.Definitions.Notifications;
using Shared.AuditLogs.Auditing;
using Shared.Uow.Attributes;

namespace Notifications.Controllers.WhatsAppProviderConfig;

[ApiController]
[Route("api/notifications/whatsapp-config")]
[Authorize]
public class WhatsAppProviderConfigController : ControllerBase
{
    private readonly IWhatsAppProviderConfigAppService _appService;

    public WhatsAppProviderConfigController(IWhatsAppProviderConfigAppService appService)
    {
        _appService = appService;
    }

    [HttpGet]
    [Authorize(Policy = NotificationsPermissions.Config.WhatsApp)]
    public Task<List<WhatsAppProviderConfigDto>> GetListAsync([FromQuery] Guid? tenantId)
    {
        return _appService.GetListAsync(tenantId);
    }

    [HttpGet("current-tenant")]
    [Authorize(Policy = NotificationsPermissions.Config.WhatsApp)]
    public Task<List<WhatsAppProviderConfigDto>> GetCurrentTenantListAsync()
    {
        return _appService.GetCurrentTenantListAsync();
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = NotificationsPermissions.Config.WhatsApp)]
    public Task<WhatsAppProviderConfigDto> GetAsync(Guid id)
    {
        return _appService.GetAsync(id);
    }

    [HttpPost]
    [Audited]
    [Authorize(Policy = NotificationsPermissions.Config.WhatsApp)]
    public Task<WhatsAppProviderConfigDto> CreateAsync([FromBody] CreateWhatsAppProviderConfigDto input)
    {
        return _appService.CreateAsync(input);
    }

    [HttpPost("current-tenant")]
    [Audited]
    [Authorize(Policy = NotificationsPermissions.Config.WhatsApp)]
    public Task<WhatsAppProviderConfigDto> CreateForCurrentTenantAsync([FromBody] CreateWhatsAppProviderConfigDto input)
    {
        return _appService.CreateForCurrentTenantAsync(input);
    }

    [HttpPut("{id:guid}")]
    [Audited]
    [Authorize(Policy = NotificationsPermissions.Config.WhatsApp)]
    public Task<WhatsAppProviderConfigDto> UpdateAsync(Guid id, [FromBody] UpdateWhatsAppProviderConfigDto input)
    {
        return _appService.UpdateAsync(id, input);
    }

    [HttpDelete("{id:guid}")]
    [Audited]
    [Authorize(Policy = NotificationsPermissions.Config.WhatsApp)]
    public Task DeleteAsync(Guid id)
    {
        return _appService.DeleteAsync(id);
    }

    [HttpPost("{id:guid}/set-default")]
    [Audited]
    [Authorize(Policy = NotificationsPermissions.Config.WhatsApp)]
    public Task<WhatsAppProviderConfigDto> SetDefaultAsync(Guid id)
    {
        return _appService.SetDefaultAsync(id);
    }

    [HttpPost("{id:guid}/test-send")]
    [UnitOfWork(isTransactional: false)]
    [Audited]
    [Authorize(Policy = NotificationsPermissions.Config.TestSend)]
    public Task<WhatsAppProviderConfigTestSendResultDto> TestSendAsync(Guid id, [FromBody] WhatsAppProviderConfigTestSendInput input)
    {
        return _appService.SendTestAsync(id, input);
    }
}
