using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Identity.Application.Contracts.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Validation.AspNetCore;
using Shared.AuditLogs.Auditing;
using Shared.Permissions.Definitions.Identity;

namespace Identity.Controllers.LinkUser;

[ApiController]
[Route("api/identity/link-users")]
[Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)]
public class LinkUserController : ControllerBase
{
    private readonly IIdentityLinkUserAppService _linkUserAppService;

    public LinkUserController(IIdentityLinkUserAppService linkUserAppService)
    {
        _linkUserAppService = linkUserAppService;
    }

    [HttpGet]
    public Task<List<IdentityLinkUserDto>> GetListAsync()
    {
        return _linkUserAppService.GetListAsync();
    }

    [HttpGet("admin")]
    [Authorize(Policy = IdentityPermissions.Users.Update)]
    public Task<List<IdentityLinkUserDto>> GetListForUserAsync([FromQuery] Guid userId)
    {
        return _linkUserAppService.GetListForUserAsync(userId);
    }

    [HttpGet("requests")]
    public Task<List<IdentityLinkUserRequestDto>> GetRequestsAsync()
    {
        return _linkUserAppService.GetRequestsAsync();
    }

    [HttpGet("admin/requests")]
    [Authorize(Policy = IdentityPermissions.Users.Update)]
    public Task<List<IdentityLinkUserRequestDto>> GetRequestsForUserAsync([FromQuery] Guid userId)
    {
        return _linkUserAppService.GetRequestsForUserAsync(userId);
    }

    [HttpPost("requests")]
    [Audited]
    public Task CreateRequestAsync([FromBody] CreateLinkUserRequestInput input)
    {
        return _linkUserAppService.CreateRequestAsync(input);
    }

    [HttpPost("admin/requests")]
    [Authorize(Policy = IdentityPermissions.Users.Update)]
    [Audited]
    public Task CreateRequestForUserAsync([FromBody] CreateLinkUserRequestForUserInput input)
    {
        return _linkUserAppService.CreateRequestForUserAsync(input);
    }

    [HttpPost("requests/{id:guid}/approve")]
    [Audited]
    public Task ApproveRequestAsync(Guid id)
    {
        return _linkUserAppService.ApproveRequestAsync(id);
    }

    [HttpPost("admin/requests/{id:guid}/approve")]
    [Authorize(Policy = IdentityPermissions.Users.Update)]
    [Audited]
    public Task ApproveRequestForUserAsync(Guid id, [FromBody] LinkUserRequestAdminDecisionInput input)
    {
        return _linkUserAppService.ApproveRequestForUserAsync(id, input);
    }

    [HttpPost("requests/{id:guid}/reject")]
    [Audited]
    public Task RejectRequestAsync(Guid id, [FromBody] RejectLinkUserRequestInput? input = null)
    {
        return _linkUserAppService.RejectRequestAsync(id, input);
    }

    [HttpPost("admin/requests/{id:guid}/reject")]
    [Authorize(Policy = IdentityPermissions.Users.Update)]
    [Audited]
    public Task RejectRequestForUserAsync(Guid id, [FromBody] RejectLinkUserRequestForUserInput input)
    {
        return _linkUserAppService.RejectRequestForUserAsync(id, input);
    }

    [HttpPost("link")]
    [Audited]
    public Task LinkAsync([FromBody] LinkUserDto input)
    {
        return _linkUserAppService.LinkAsync(input);
    }

    [HttpPost("unlink")]
    [Audited]
    public Task UnlinkAsync([FromBody] LinkUserDto input)
    {
        return _linkUserAppService.UnlinkAsync(input);
    }

    [HttpPost("admin/unlink")]
    [Authorize(Policy = IdentityPermissions.Users.Update)]
    [Audited]
    public Task UnlinkForUserAsync([FromBody] LinkUserAdminUnlinkInput input)
    {
        return _linkUserAppService.UnlinkForUserAsync(input);
    }
}
