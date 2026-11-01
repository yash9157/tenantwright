using Identity.Application.Contracts.Passkeys;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OpenIddict.Validation.AspNetCore;
using Shared.AuditLogs.Auditing;

namespace Identity.Controllers.Profile;

[ApiController]
[Route("api/identity/profile/passkeys")]
[Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)]
[EnableRateLimiting("identity-passkeys")]
public sealed class PasskeysController : ControllerBase
{
    private readonly IPasskeyAppService _passkeyAppService;

    public PasskeysController(IPasskeyAppService passkeyAppService)
    {
        _passkeyAppService = passkeyAppService;
    }

    [HttpGet]
    public Task<List<PasskeyCredentialDto>> GetListAsync()
        => _passkeyAppService.GetListAsync();

    [HttpPost("registration/options")]
    [DisableAuditing]
    public Task<PasskeyCeremonyOptionsDto> BeginRegistrationAsync([FromBody] BeginPasskeyRegistrationInput input)
        => _passkeyAppService.BeginRegistrationAsync(input);

    [HttpPost("registration/complete")]
    [DisableAuditing]
    public Task<PasskeyCredentialDto> CompleteRegistrationAsync([FromBody] CompletePasskeyRegistrationInput input)
        => _passkeyAppService.CompleteRegistrationAsync(input);

    [HttpPut("{credentialId:guid}")]
    [Audited]
    public Task<PasskeyCredentialDto> RenameAsync(Guid credentialId, [FromBody] RenamePasskeyInput input)
        => _passkeyAppService.RenameAsync(credentialId, input);

    [HttpPost("{credentialId:guid}/revoke")]
    [DisableAuditing]
    public Task RevokeAsync(Guid credentialId, [FromBody] RevokePasskeyInput input)
        => _passkeyAppService.RevokeAsync(credentialId, input);
}
