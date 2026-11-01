using Identity.Application.Contracts.Invitations;
using Identity.Application.Contracts.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Validation.AspNetCore;
using Shared.AuditLogs.Auditing;
using Shared.Permissions.Definitions.Identity;

namespace Identity.Controllers.Invitations;

[ApiController]
[Route("api/identity/invitations")]
[Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.Users.Default)]
public sealed class InvitationsController : ControllerBase
{
    private readonly IInvitationAppService _invitationAppService;

    public InvitationsController(IInvitationAppService invitationAppService)
    {
        _invitationAppService = invitationAppService;
    }

    [HttpGet]
    public Task<PagedResultDto<InvitationDto>> GetListAsync([FromQuery] InvitationListInput input)
    {
        return _invitationAppService.GetListAsync(input);
    }

    [HttpPost]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.Users.Create)]
    [Audited]
    public Task<InvitationDto> CreateAsync([FromBody] CreateInvitationInput input)
    {
        return _invitationAppService.CreateAsync(input);
    }

    [HttpPost("batch/validate")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.Users.Create)]
    public Task<InvitationBatchValidationDto> ValidateBatchAsync([FromBody] ValidateInvitationBatchInput input)
    {
        return _invitationAppService.ValidateBatchAsync(input);
    }

    [HttpPost("batch")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.Users.Create)]
    [Audited]
    public Task<InvitationBatchResultDto> CreateBatchAsync([FromBody] CreateInvitationBatchInput input)
    {
        return _invitationAppService.CreateBatchAsync(input);
    }

    [HttpPost("{id:guid}/resend")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.Users.Update)]
    [Audited]
    public Task<InvitationDto> ResendAsync(Guid id, [FromBody] ResendInvitationInput input)
    {
        return _invitationAppService.ResendAsync(id, input);
    }

    [HttpDelete("{id:guid}")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, Policy = IdentityPermissions.Users.Update)]
    [Audited]
    public Task CancelAsync(Guid id)
    {
        return _invitationAppService.CancelAsync(id);
    }
}
