using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Notifications.Application.Contracts.Emailing;
using Shared.Permissions.Definitions.Notifications;
using Shared.AuditLogs.Auditing;
using Shared.Uow.Attributes;

namespace Notifications.Controllers.EmailProviderConfig;

[ApiController]
[Route("api/notifications/email-config")]
[Authorize]
public class EmailProviderConfigController : ControllerBase
{
    private readonly IEmailProviderConfigAppService _appService;

    public EmailProviderConfigController(IEmailProviderConfigAppService appService)
    {
        _appService = appService;
    }

    [HttpGet]
    [Authorize(Policy = NotificationsPermissions.Config.Email)]
    public Task<List<EmailProviderConfigDto>> GetListAsync([FromQuery] Guid? tenantId)
    {
        return _appService.GetListAsync(tenantId);
    }

    [HttpGet("current-tenant")]
    [Authorize(Policy = NotificationsPermissions.Config.Email)]
    public Task<List<EmailProviderConfigDto>> GetCurrentTenantListAsync()
    {
        return _appService.GetCurrentTenantListAsync();
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = NotificationsPermissions.Config.Email)]
    public Task<EmailProviderConfigDto> GetAsync(Guid id)
    {
        return _appService.GetAsync(id);
    }

    [HttpPost]
    [Audited]
    [Authorize(Policy = NotificationsPermissions.Config.Email)]
    public Task<EmailProviderConfigDto> CreateAsync([FromBody] CreateEmailProviderConfigDto input)
    {
        return _appService.CreateAsync(input);
    }

    [HttpPost("current-tenant")]
    [Audited]
    [Authorize(Policy = NotificationsPermissions.Config.Email)]
    public Task<EmailProviderConfigDto> CreateForCurrentTenantAsync([FromBody] CreateEmailProviderConfigDto input)
    {
        return _appService.CreateForCurrentTenantAsync(input);
    }

    [HttpPut("{id:guid}")]
    [Audited]
    [Authorize(Policy = NotificationsPermissions.Config.Email)]
    public Task<EmailProviderConfigDto> UpdateAsync(Guid id, [FromBody] UpdateEmailProviderConfigDto input)
    {
        return _appService.UpdateAsync(id, input);
    }

    [HttpDelete("{id:guid}")]
    [Audited]
    [Authorize(Policy = NotificationsPermissions.Config.Email)]
    public Task DeleteAsync(Guid id)
    {
        return _appService.DeleteAsync(id);
    }

    [HttpPost("{id:guid}/set-default")]
    [Audited]
    [Authorize(Policy = NotificationsPermissions.Config.Email)]
    public Task<EmailProviderConfigDto> SetDefaultAsync(Guid id)
    {
        return _appService.SetDefaultAsync(id);
    }

    [HttpPost("{id:guid}/test-send")]
    [UnitOfWork(isTransactional: false)]
    [Audited]
    [Authorize(Policy = NotificationsPermissions.Config.TestSend)]
    public Task<EmailProviderConfigTestSendResultDto> TestSendAsync(Guid id, [FromBody] EmailProviderConfigTestSendInput input)
    {
        return _appService.SendTestAsync(id, input);
    }
}
