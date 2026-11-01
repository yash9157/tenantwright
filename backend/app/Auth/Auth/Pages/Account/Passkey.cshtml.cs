using System.ComponentModel.DataAnnotations;
using Auth.Claims;
using Auth.Clients;
using Auth.Routing;
using Identity.Application.Contracts.Passkeys;
using Identity.Domain.Shared.Account;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OpenIddict.Abstractions;

namespace Auth.Pages.Account;

[EnableRateLimiting("auth-mfa")]
public sealed class PasskeyModel : AuthInteractivePageModelBase
{
    private readonly IIdentityClient _identityClient;

    public PasskeyModel(
        IIdentityClient identityClient,
        ITenantClient tenantClient,
        IOpenIddictClaimsPrincipalHandler claimsPrincipalHandler,
        IOpenIddictApplicationManager applicationManager)
        : base(identityClient, tenantClient, claimsPrincipalHandler, applicationManager)
    {
        _identityClient = identityClient;
    }

    [BindProperty]
    public PasskeyInputModel Input { get; set; } = new();

    public string? ErrorMessage { get; private set; }
    public string? OptionsJson { get; private set; }
    public bool HasChallengeContext { get; private set; } = true;
    public bool IsEnrollment { get; private set; }
    public bool AuthenticatorCodeAvailable { get; private set; }
    public bool RecoveryCodeAvailable { get; private set; }
    public bool CodeBasedMfaAvailable => AuthenticatorCodeAvailable || RecoveryCodeAvailable;
    public string AlternativeMfaRoute =>
        AuthenticatorCodeAvailable ? AuthPageRoutes.Mfa : AuthPageRoutes.RecoveryCode;
    public string AlternativeMfaLabel =>
        AuthenticatorCodeAvailable ? "Use an authenticator code" : "Use a recovery code";

    public IActionResult OnGet()
    {
        var state = InteractiveLoginFlowStateStore.Read(TempData, keep: true);
        ApplyState(state);
        return Page();
    }

    public async Task<IActionResult> OnPostBeginAsync()
    {
        var state = InteractiveLoginFlowStateStore.Read(TempData, keep: false);
        if (!ApplyState(state) || state == null)
        {
            return Page();
        }

        try
        {
            var request = CreateOptionsInput(state);
            var options = state.PasskeyEnrollmentRequired
                ? await _identityClient.BeginInteractivePasskeyEnrollmentAsync(request, state.TenantId, HttpContext.RequestAborted)
                : await _identityClient.BeginInteractivePasskeyAssertionAsync(request, state.TenantId, HttpContext.RequestAborted);

            Input.CeremonyId = options.CeremonyId;
            OptionsJson = options.OptionsJson;
            InteractiveLoginFlowStateStore.Save(TempData, state);
        }
        catch
        {
            ErrorMessage = "The passkey ceremony could not be started. Sign in again or use another verification method.";
            InteractiveLoginFlowStateStore.Save(TempData, state);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostCompleteAsync()
    {
        var state = InteractiveLoginFlowStateStore.Read(TempData, keep: false);
        if (!ApplyState(state) || state == null)
        {
            return Page();
        }

        if (Input.CeremonyId == Guid.Empty || string.IsNullOrWhiteSpace(Input.ResponseJson))
        {
            ErrorMessage = "The authenticator response was missing. Start the passkey step again.";
            InteractiveLoginFlowStateStore.Save(TempData, state);
            return Page();
        }

        try
        {
            var request = new CompleteInteractivePasskeyInput
            {
                Mode = state.Mode,
                TenantId = state.TenantId,
                ChallengeId = state.ChallengeId,
                CeremonyId = Input.CeremonyId,
                ResponseJson = Input.ResponseJson
            };
            var result = state.PasskeyEnrollmentRequired
                ? await _identityClient.CompleteInteractivePasskeyEnrollmentAsync(request, state.TenantId, HttpContext.RequestAborted)
                : await _identityClient.CompleteInteractivePasskeyAssertionAsync(request, state.TenantId, HttpContext.RequestAborted);

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
            }
            else if (result.Step == InteractiveLoginStep.Locked)
            {
                return Redirect($"{AuthPageRoutes.Locked}?reason=locked");
            }
            else if (result.Step == InteractiveLoginStep.Blocked)
            {
                return Redirect($"{AuthPageRoutes.Locked}?reason=blocked");
            }
            else if (result.Step == InteractiveLoginStep.PasswordExpired && result.ChallengeId.HasValue)
            {
                state.ChallengeId = result.ChallengeId;
                state.ChallengeExpiresAtUtc = result.ChallengeExpiresAtUtc;
                InteractiveLoginFlowStateStore.Save(TempData, state);
                return Redirect(AuthPageRoutes.PasswordExpired);
            }
            else if (result.Step == InteractiveLoginStep.RequiresMfa &&
                     result.ChallengeId.HasValue &&
                     result.CodeBasedMfaAvailable)
            {
                state.ChallengeId = result.ChallengeId;
                state.ChallengeExpiresAtUtc = result.ChallengeExpiresAtUtc;
                state.PasskeyAvailable = false;
                state.AuthenticatorCodeAvailable = result.AuthenticatorCodeAvailable;
                state.RecoveryCodeAvailable = result.RecoveryCodeAvailable;
                state.PasskeyEnrollmentRequired = false;
                InteractiveLoginFlowStateStore.Save(TempData, state);
                return Redirect(
                    result.AuthenticatorCodeAvailable
                        ? AuthPageRoutes.Mfa
                        : result.RecoveryCodeAvailable
                            ? AuthPageRoutes.RecoveryCode
                            : AuthPageRoutes.Passkey);
            }
            else
            {
                ErrorMessage = result.Message ?? "Passkey verification failed. Start the passkey step again.";
            }
        }
        catch
        {
            ErrorMessage = "Passkey verification failed. The ceremony was not accepted; start the step again.";
        }

        InteractiveLoginFlowStateStore.Save(TempData, state);
        return Page();
    }

    private bool ApplyState(InteractiveLoginFlowState? state)
    {
        HasChallengeContext = state?.ChallengeId.HasValue == true &&
                              (state.PasskeyAvailable || state.PasskeyEnrollmentRequired);
        IsEnrollment = state?.PasskeyEnrollmentRequired == true;
        AuthenticatorCodeAvailable = state?.AuthenticatorCodeAvailable == true;
        RecoveryCodeAvailable = state?.RecoveryCodeAvailable == true;
        return HasChallengeContext;
    }

    private static InteractivePasskeyOptionsInput CreateOptionsInput(InteractiveLoginFlowState state)
        => new()
        {
            Mode = state.Mode,
            TenantId = state.TenantId,
            ChallengeId = state.ChallengeId
        };

    public sealed class PasskeyInputModel
    {
        public Guid CeremonyId { get; set; }

        [StringLength(65536)]
        public string? ResponseJson { get; set; }
    }
}
