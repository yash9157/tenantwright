using System.ComponentModel.DataAnnotations;
using Auth.Claims;
using Auth.Clients;
using Auth.Routing;
using Auth.Pages.Account.Validation;
using Identity.Application.Contracts.Account;
using Identity.Domain.Shared.Account;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OpenIddict.Abstractions;

namespace Auth.Pages.Account;

[EnableRateLimiting("auth-mfa")]
public class MfaModel : AuthInteractivePageModelBase
{
    private readonly IIdentityClient _identityClient;
    private readonly IAuthPageValidationService _pageValidationService;

    public MfaModel(
        IIdentityClient identityClient,
        ITenantClient tenantClient,
        IAuthPageValidationService pageValidationService,
        IOpenIddictClaimsPrincipalHandler claimsPrincipalHandler,
        IOpenIddictApplicationManager applicationManager)
        : base(identityClient, tenantClient, claimsPrincipalHandler, applicationManager)
    {
        _identityClient = identityClient;
        _pageValidationService = pageValidationService;
    }

    [BindProperty]
    public MfaInputModel Input { get; set; } = new();

    public string? ErrorMessage { get; set; }
    public bool HasChallengeContext { get; private set; } = true;
    public bool RecoveryCodeAvailable { get; private set; }

    public IActionResult OnGet()
    {
        var state = InteractiveLoginFlowStateStore.Read(TempData, keep: true);
        HasChallengeContext = state?.ChallengeId.HasValue == true &&
                              state.AuthenticatorCodeAvailable;
        RecoveryCodeAvailable = state?.RecoveryCodeAvailable == true;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var state = InteractiveLoginFlowStateStore.Read(TempData, keep: false);
        if (state == null || !state.ChallengeId.HasValue || !state.AuthenticatorCodeAvailable)
        {
            HasChallengeContext = false;
            return Page();
        }

        HasChallengeContext = true;
        RecoveryCodeAvailable = state.RecoveryCodeAvailable;
        Input.TwoFactorCode = AuthInputConstraints.NormalizeOtp(Input.TwoFactorCode);
        Input.RecoveryCode = AuthInputConstraints.NormalizeRecoveryCode(Input.RecoveryCode);

        ModelState.Clear();
        if (!await this.TryValidateInputAsync(_pageValidationService, Input, nameof(Input), HttpContext.RequestAborted))
        {
            InteractiveLoginFlowStateStore.Save(TempData, state);
            return Page();
        }

        var result = await _identityClient.CompleteMfaAsync(
            new CompleteMfaInput
            {
                Mode = state.Mode,
                TenantId = state.TenantId,
                ChallengeId = state.ChallengeId,
                TwoFactorCode = Input.TwoFactorCode?.Trim(),
                TwoFactorRecoveryCode = Input.RecoveryCode?.Trim(),
                RememberTrustedDevice = Input.RememberTrustedDevice
            },
            state.TenantId,
            HttpContext.RequestAborted);

        switch (result.Step)
        {
            case InteractiveLoginStep.Succeeded:
                {
                    state.AuthenticationMethod = "mfa";
                    state.AuthenticatedAtUtc = DateTimeOffset.UtcNow;
                    var finalize = await FinalizeSignInAsync(state, HttpContext.RequestAborted);
                    if (finalize.Success && finalize.Result != null)
                    {
                        return finalize.Result;
                    }

                    ErrorMessage = finalize.Error ?? "Could not complete sign in.";
                    InteractiveLoginFlowStateStore.Save(TempData, state);
                    return Page();
                }
            case InteractiveLoginStep.PasswordExpired:
                state.ChallengeId = result.ChallengeId;
                state.ChallengeExpiresAtUtc = result.ChallengeExpiresAtUtc;
                InteractiveLoginFlowStateStore.Save(TempData, state);
                return Redirect(AuthPageRoutes.PasswordExpired);
            case InteractiveLoginStep.Locked:
                return Redirect($"{AuthPageRoutes.Locked}?reason=locked");
            case InteractiveLoginStep.Blocked:
                return Redirect($"{AuthPageRoutes.Locked}?reason=blocked");
            default:
                ErrorMessage = result.Message ?? "Invalid two-factor authentication code.";
                if (!result.ChallengeId.HasValue)
                {
                    HasChallengeContext = false;
                    return Page();
                }

                state.ChallengeId = result.ChallengeId;
                state.ChallengeExpiresAtUtc = result.ChallengeExpiresAtUtc;
                InteractiveLoginFlowStateStore.Save(TempData, state);
                return Page();
        }
    }

    public sealed class MfaInputModel
    {
        [Display(Name = "Authenticator Code")]
        [StringLength(AuthInputConstraints.OtpCodeMaxLength)]
        public string? TwoFactorCode { get; set; }

        [Display(Name = "Recovery Code")]
        [StringLength(AuthInputConstraints.RecoveryCodeMaxLength)]
        public string? RecoveryCode { get; set; }

        [Display(Name = "Trust this device")]
        public bool RememberTrustedDevice { get; set; }
    }
}
