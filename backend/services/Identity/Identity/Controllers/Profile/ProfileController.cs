using System;
using System.Threading.Tasks;
using Identity.Application.Contracts.Account;
using Identity.Application.Contracts.SecurityLogs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Validation.AspNetCore;
using Shared.AuditLogs.Auditing;

namespace Identity.Controllers.Profile;

[ApiController]
[Route("api/identity/profile")]
[Authorize(AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)]
public class ProfileController : ControllerBase
{
    private readonly IProfileAppService _profileAppService;

    public ProfileController(IProfileAppService profileAppService)
    {
        _profileAppService = profileAppService;
    }

    [HttpGet]
    public Task<ProfileDto> GetAsync()
    {
        return _profileAppService.GetAsync();
    }

    [HttpPut]
    [Audited]
    public Task<ProfileDto> UpdateAsync([FromBody] UpdateProfileDto input)
    {
        return _profileAppService.UpdateAsync(input);
    }

    [HttpPost("pending-email/resend")]
    [Audited]
    public Task<ProfileDto> ResendPendingEmailConfirmationAsync()
    {
        return _profileAppService.ResendPendingEmailConfirmationAsync();
    }

    [HttpDelete("pending-email")]
    [Audited]
    public Task<ProfileDto> CancelPendingEmailChangeAsync()
    {
        return _profileAppService.CancelPendingEmailChangeAsync();
    }

    [HttpGet("preferences")]
    public Task<UserNotificationSecurityPreferencesDto> GetPreferencesAsync()
    {
        return _profileAppService.GetPreferencesAsync();
    }

    [HttpPut("preferences")]
    [Audited]
    public Task<UserNotificationSecurityPreferencesDto> UpdatePreferencesAsync([FromBody] UpdateUserNotificationSecurityPreferencesInput input)
    {
        return _profileAppService.UpdatePreferencesAsync(input);
    }

    [HttpPost("change-password")]
    [Audited]
    public Task ChangePasswordAsync([FromBody] ChangePasswordInput input)
    {
        return _profileAppService.ChangePasswordAsync(input);
    }

    [HttpGet("mfa")]
    public Task<MfaStatusDto> GetMfaStatusAsync()
    {
        return _profileAppService.GetMfaStatusAsync();
    }

    [HttpPost("mfa/generate")]
    [Audited]
    public Task<MfaSetupDto> GenerateMfaSetupAsync([FromBody] MfaSensitiveOperationProofInput input)
    {
        return _profileAppService.GenerateMfaSetupAsync(input);
    }

    [HttpPost("mfa/enable")]
    [Audited]
    public Task<MfaSetupDto> EnableMfaAsync([FromBody] EnableMfaInput input)
    {
        return _profileAppService.EnableMfaAsync(input);
    }

    [HttpPost("mfa/disable")]
    [Audited]
    public Task DisableMfaAsync([FromBody] MfaSensitiveOperationProofInput input)
    {
        return _profileAppService.DisableMfaAsync(input);
    }

    [HttpPost("mfa/recovery-codes/regenerate")]
    [Audited]
    public Task<MfaSetupDto> RegenerateRecoveryCodesAsync([FromBody] MfaSensitiveOperationProofInput input)
    {
        return _profileAppService.RegenerateRecoveryCodesAsync(input);
    }

    [HttpGet("security-logs/paged")]
    public Task<PagedResultDto<SecurityLogDto>> GetMySecurityLogsPagedAsync([FromQuery] SecurityLogListInput input)
    {
        return _profileAppService.GetMySecurityLogsPagedAsync(input);
    }

    [HttpGet("risk-decisions/paged")]
    public Task<PagedResultDto<SecurityLogDto>> GetMyRiskDecisionsPagedAsync([FromQuery] ProfileRiskDecisionListInput input)
    {
        return _profileAppService.GetMyRiskDecisionsPagedAsync(input);
    }

    [HttpPost("risk-decisions/{securityLogId:guid}/false-positive")]
    [Audited]
    public Task MarkMyRiskDecisionFalsePositiveAsync([FromRoute] Guid securityLogId)
    {
        return _profileAppService.MarkMyRiskDecisionFalsePositiveAsync(securityLogId);
    }
}
