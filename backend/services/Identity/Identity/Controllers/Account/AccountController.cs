using System;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using Identity.Application.Contracts.Account;
using Identity.Application.Contracts.Passkeys;
using Identity.Domain.Shared.Account;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OpenIddict.Abstractions;
using Shared.AuditLogs.Auditing;

namespace Identity.Controllers.Account;

[ApiController]
[Route("api/identity/account")]
[Authorize(Policy = "IdentityInternal")]
public class AccountController : ControllerBase
{
    private readonly IAccountAppService _accountAppService;
    public AccountController(IAccountAppService accountAppService)
    {
        _accountAppService = accountAppService;
    }

    [HttpPost("check-password")]
    [DisableAuditing]
    [EnableRateLimiting("identity-auth-login")]
    public async Task<ActionResult<LoginResultDto>> CheckPasswordAsync([FromBody] CheckPasswordInput input)
    {
        if (string.IsNullOrWhiteSpace(input.LoginInput) || string.IsNullOrWhiteSpace(input.Password))
        {
            return BadRequest("Login input and password are required.");
        }

        if (!IsValidInteractiveRequest(input.Mode, input.TenantId))
        {
            return BadRequest("Invalid login mode or tenant.");
        }

        return await _accountAppService.CheckPasswordAsync(
            input.LoginInput,
            input.Password,
            tenantId: input.TenantId,
            mode: input.Mode,
            sessionId: input.SessionId,
            clientId: input.ClientId,
            clientIpAddress: input.ClientIpAddress,
            browserInfo: input.BrowserInfo,
            correlationId: input.CorrelationId,
            device: input.Device,
            location: input.Location,
            twoFactorCode: input.TwoFactorCode,
            twoFactorRecoveryCode: input.TwoFactorRecoveryCode,
            deviceFingerprint: input.DeviceFingerprint,
            rememberTrustedDevice: input.RememberTrustedDevice);
    }

    private static bool IsValidInteractiveRequest(InteractiveLoginMode mode, Guid? tenantId)
    {
        return mode switch
        {
            InteractiveLoginMode.Tenant => tenantId.HasValue && tenantId.Value != Guid.Empty,
            InteractiveLoginMode.Platform => !tenantId.HasValue,
            _ => false
        };
    }

    [HttpPost("interactive/start")]
    [DisableAuditing]
    [EnableRateLimiting("identity-auth-login")]
    public async Task<ActionResult<InteractiveLoginResultDto>> StartInteractiveLoginAsync([FromBody] InteractiveLoginStartInput input)
    {
        if (!IsValidInteractiveRequest(input.Mode, input.TenantId) ||
            string.IsNullOrWhiteSpace(input.LoginInput) ||
            string.IsNullOrWhiteSpace(input.Password))
        {
            return BadRequest("Login mode, tenant, login input, and password are required.");
        }

        return await _accountAppService.StartInteractiveLoginAsync(input);
    }

    [HttpPost("interactive/mfa/complete")]
    [DisableAuditing]
    [EnableRateLimiting("identity-auth-mfa")]
    public async Task<ActionResult<InteractiveLoginResultDto>> CompleteMfaAsync([FromBody] CompleteMfaInput input)
    {
        if (!IsValidInteractiveRequest(input.Mode, input.TenantId))
        {
            return BadRequest("Invalid login mode or tenant.");
        }

        if (string.IsNullOrWhiteSpace(input.TwoFactorCode) && string.IsNullOrWhiteSpace(input.TwoFactorRecoveryCode))
        {
            return BadRequest("Two-factor code or recovery code is required.");
        }

        return await _accountAppService.CompleteMfaAsync(input);
    }

    [HttpPost("interactive/trust-device/complete")]
    [DisableAuditing]
    [EnableRateLimiting("identity-auth-mfa")]
    public async Task<ActionResult<InteractiveLoginResultDto>> CompleteTrustDeviceAsync([FromBody] CompleteTrustDeviceInput input)
    {
        if (!IsValidInteractiveRequest(input.Mode, input.TenantId))
        {
            return BadRequest("Invalid login mode or tenant.");
        }

        return await _accountAppService.CompleteTrustDeviceAsync(input);
    }

    [HttpPost("interactive/passkey/enrollment/options")]
    [DisableAuditing]
    [EnableRateLimiting("identity-auth-mfa")]
    public async Task<ActionResult<PasskeyCeremonyOptionsDto>> BeginPasskeyEnrollmentAsync([FromBody] InteractivePasskeyOptionsInput input)
    {
        if (!IsValidPasskeyChallenge(input))
        {
            return BadRequest("A valid login challenge is required.");
        }

        return await _accountAppService.BeginInteractivePasskeyEnrollmentAsync(input);
    }

    [HttpPost("interactive/passkey/enrollment/complete")]
    [DisableAuditing]
    [EnableRateLimiting("identity-auth-mfa")]
    public async Task<ActionResult<InteractiveLoginResultDto>> CompletePasskeyEnrollmentAsync([FromBody] CompleteInteractivePasskeyInput input)
    {
        if (!IsValidPasskeyCompletion(input))
        {
            return BadRequest("A valid login challenge, ceremony, and authenticator response are required.");
        }

        return await _accountAppService.CompleteInteractivePasskeyEnrollmentAsync(input);
    }

    [HttpPost("interactive/passkey/assertion/options")]
    [DisableAuditing]
    [EnableRateLimiting("identity-auth-mfa")]
    public async Task<ActionResult<PasskeyCeremonyOptionsDto>> BeginPasskeyAssertionAsync([FromBody] InteractivePasskeyOptionsInput input)
    {
        if (!IsValidPasskeyChallenge(input))
        {
            return BadRequest("A valid login challenge is required.");
        }

        return await _accountAppService.BeginInteractivePasskeyAssertionAsync(input);
    }

    [HttpPost("interactive/passkey/assertion/complete")]
    [DisableAuditing]
    [EnableRateLimiting("identity-auth-mfa")]
    public async Task<ActionResult<InteractiveLoginResultDto>> CompletePasskeyAssertionAsync([FromBody] CompleteInteractivePasskeyInput input)
    {
        if (!IsValidPasskeyCompletion(input))
        {
            return BadRequest("A valid login challenge, ceremony, and authenticator response are required.");
        }

        return await _accountAppService.CompleteInteractivePasskeyAssertionAsync(input);
    }

    private static bool IsValidPasskeyChallenge(InteractiveChallengeInput input)
        => IsValidInteractiveRequest(input.Mode, input.TenantId) &&
           input.ChallengeId.HasValue &&
           input.ChallengeId.Value != Guid.Empty;

    private static bool IsValidPasskeyCompletion(CompleteInteractivePasskeyInput input)
        => IsValidPasskeyChallenge(input) &&
           input.CeremonyId != Guid.Empty &&
           !string.IsNullOrWhiteSpace(input.ResponseJson);

    [HttpPost("password-reset/request")]
    [Audited]
    [EnableRateLimiting("identity-auth-reset")]
    public async Task<IActionResult> RequestPasswordResetAsync([FromBody] PasswordResetRequestInput input)
    {
        if (input.TenantId == Guid.Empty || string.IsNullOrWhiteSpace(input.LoginInput))
        {
            return BadRequest("Tenant and login input are required.");
        }

        await _accountAppService.RequestPasswordResetAsync(input);
        return Accepted(new { accepted = true });
    }

    [HttpPost("password-reset/validate")]
    [DisableAuditing]
    [EnableRateLimiting("identity-auth-reset")]
    public async Task<ActionResult<PasswordResetValidateResultDto>> ValidatePasswordResetAsync([FromBody] PasswordResetValidateInput input)
    {
        if (input.TenantId == Guid.Empty || input.UserId == Guid.Empty || string.IsNullOrWhiteSpace(input.Token))
        {
            return BadRequest("Tenant, user id, and token are required.");
        }

        return await _accountAppService.ValidatePasswordResetAsync(input);
    }

    [HttpPost("password-reset/complete")]
    [DisableAuditing]
    [EnableRateLimiting("identity-auth-reset")]
    public async Task<ActionResult<PasswordResetCompleteResultDto>> CompletePasswordResetAsync([FromBody] PasswordResetCompleteInput input)
    {
        if (input.TenantId == Guid.Empty || input.UserId == Guid.Empty || string.IsNullOrWhiteSpace(input.Token))
        {
            return BadRequest("Tenant, user id, and token are required.");
        }

        return await _accountAppService.CompletePasswordResetAsync(input);
    }

    [HttpPost("password-expired/change")]
    [DisableAuditing]
    [EnableRateLimiting("identity-auth-mfa")]
    public async Task<ActionResult<PasswordExpiredChangeResultDto>> ChangeExpiredPasswordAsync([FromBody] PasswordExpiredChangeInput input)
    {
        if (!IsValidInteractiveRequest(input.Mode, input.TenantId))
        {
            return BadRequest("Invalid login mode or tenant.");
        }

        return await _accountAppService.ChangeExpiredPasswordAsync(input);
    }

    [HttpPost("email-verification/request")]
    [Audited]
    [EnableRateLimiting("identity-auth-reset")]
    public async Task<IActionResult> RequestEmailVerificationAsync([FromBody] EmailVerificationRequestInput input)
    {
        if (input.TenantId == Guid.Empty || string.IsNullOrWhiteSpace(input.LoginInput))
        {
            return BadRequest("Tenant and login input are required.");
        }

        await _accountAppService.RequestEmailVerificationAsync(input);
        return Accepted(new { accepted = true });
    }

    [HttpPost("email-verification/confirm")]
    [DisableAuditing]
    [EnableRateLimiting("identity-auth-reset")]
    public async Task<ActionResult<EmailVerificationResultDto>> ConfirmEmailVerificationAsync([FromBody] EmailVerificationConfirmInput input)
    {
        if (input.TenantId == Guid.Empty || input.UserId == Guid.Empty || string.IsNullOrWhiteSpace(input.Token))
        {
            return BadRequest("Tenant, user id, and token are required.");
        }

        return await _accountAppService.ConfirmEmailVerificationAsync(input);
    }

    [HttpPost("email-verification/resend")]
    [Audited]
    [EnableRateLimiting("identity-auth-reset")]
    public async Task<IActionResult> ResendEmailVerificationAsync([FromBody] EmailVerificationRequestInput input)
    {
        if (input.TenantId == Guid.Empty || string.IsNullOrWhiteSpace(input.LoginInput))
        {
            return BadRequest("Tenant and login input are required.");
        }

        await _accountAppService.ResendEmailVerificationAsync(input);
        return Accepted(new { accepted = true });
    }

    [HttpPost("public-invitations/validate")]
    [DisableAuditing]
    [EnableRateLimiting("identity-auth-reset")]
    public async Task<ActionResult<PublicInvitationValidateResultDto>> ValidatePublicInvitationAsync([FromBody] PublicInvitationValidateInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Token))
        {
            return BadRequest("Invitation token is required.");
        }

        return await _accountAppService.ValidatePublicInvitationAsync(input);
    }

    [HttpPost("public-invitations/accept")]
    [DisableAuditing]
    [EnableRateLimiting("identity-auth-reset")]
    public async Task<ActionResult<PublicInvitationAcceptResultDto>> AcceptPublicInvitationAsync([FromBody] PublicInvitationAcceptInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Token))
        {
            return BadRequest("Invitation token is required.");
        }

        return await _accountAppService.AcceptPublicInvitationAsync(input);
    }

    [HttpGet("user-info")]
    public async Task<ActionResult<UserInfoDto?>> GetUserInfoAsync(
        [Required] string loginInput,
        Guid? tenantId = null,
        InteractiveLoginMode mode = InteractiveLoginMode.Tenant)
    {
        if (string.IsNullOrWhiteSpace(loginInput))
        {
            return BadRequest("Login input is required.");
        }

        if (!IsValidInteractiveRequest(mode, tenantId))
        {
            return BadRequest("Invalid login mode or tenant.");
        }

        var userInfo = await _accountAppService.GetUserInfoAsync(loginInput, tenantId, mode);
        if (userInfo == null)
        {
            return NotFound();
        }

        return userInfo;
    }

}
