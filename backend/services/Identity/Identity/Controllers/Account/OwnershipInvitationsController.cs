using Identity.Application.Contracts.Account;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Validation.AspNetCore;

namespace Identity.Controllers.Account;

[ApiController]
[Route("api/identity/ownership-invitations")]
[Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)]
public sealed class OwnershipInvitationsController : ControllerBase
{
    private readonly IAccountAppService _accountAppService;
    public OwnershipInvitationsController(IAccountAppService accountAppService) => _accountAppService = accountAppService;

    [HttpPost("accept")]
    public Task<PublicInvitationAcceptResultDto> AcceptAsync([FromBody] OwnershipInvitationAcceptInput input)
        => _accountAppService.AcceptOwnershipInvitationAsync(input);
}
