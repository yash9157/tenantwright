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
public class RecoveryCodeModel : AuthInteractivePageModelBase
{
    private readonly IIdentityClient _identityClient;
    private readonly IAuthPageValidationService _pageValidationService;

    public RecoveryCodeModel(
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
    public RecoveryCodeInput Input { get; set; } = new();

    public string? ErrorMessage { get; set; }
    public bool HasChallengeContext { get; private set; } = true;
    public bool AuthenticatorCodeAvailable { get; private set; }

    public IActionResult OnGet()
    {
        var state = InteractiveLoginFlowStateStore.Read(TempData, keep: true);
        HasChallengeContext = state?.ChallengeId.HasValue == true &&
                              state.RecoveryCodeAvailable;
        AuthenticatorCodeAvailable = state?.AuthenticatorCodeAvailable == true;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var state = InteractiveLoginFlowStateStore.Read(TempData, keep: false);
        if (state == null || !state.ChallengeId.HasValue || !state.RecoveryCodeAvailable)
        {
            HasChallengeContext = false;
            return Page();
        }

        HasChallengeContext = true;
        AuthenticatorCodeAvailable = state.AuthenticatorCodeAvailable;
        Input.RecoveryCode = AuthInputConstraints.NormalizeRecoveryCode(Input.RecoveryCode) ?? string.Empty;

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
                TwoFactorRecoveryCode = Input.RecoveryCode?.Trim(),
                RememberTrustedDevice = Input.RememberTrustedDevice
            },
            state.TenantId,
            HttpContext.RequestAborted);

        if (result.Step == InteractiveLoginStep.Succeeded)
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

        if (result.Step == InteractiveLoginStep.Locked)
        {
            return Redirect($"{AuthPageRoutes.Locked}?reason=locked");
        }

        if (result.Step == InteractiveLoginStep.Blocked)
        {
            return Redirect($"{AuthPageRoutes.Locked}?reason=blocked");
        }

        if (result.Step == InteractiveLoginStep.PasswordExpired)
        {
            state.ChallengeId = result.ChallengeId;
            state.ChallengeExpiresAtUtc = result.ChallengeExpiresAtUtc;
            InteractiveLoginFlowStateStore.Save(TempData, state);
            return Redirect(AuthPageRoutes.PasswordExpired);
        }

        ErrorMessage = result.Message ?? "Recovery code is invalid.";
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

    public sealed class RecoveryCodeInput
    {
        [Required]
        [StringLength(AuthInputConstraints.RecoveryCodeMaxLength)]
        [Display(Name = "Recovery Code")]
        public string RecoveryCode { get; set; } = string.Empty;

        [Display(Name = "Trust this device")]
        public bool RememberTrustedDevice { get; set; }
    }
}
