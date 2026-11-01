using Auth.Claims;
using Auth.Clients;
using Auth.Routing;
using Identity.Application.Contracts.Account;
using Identity.Domain.Shared.Account;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OpenIddict.Abstractions;

namespace Auth.Pages.Account;

[EnableRateLimiting("auth-mfa")]
public class TrustDeviceModel : AuthInteractivePageModelBase
{
    private readonly IIdentityClient _identityClient;

    public TrustDeviceModel(
        IIdentityClient identityClient,
        ITenantClient tenantClient,
        IOpenIddictClaimsPrincipalHandler claimsPrincipalHandler,
        IOpenIddictApplicationManager applicationManager)
        : base(identityClient, tenantClient, claimsPrincipalHandler, applicationManager)
    {
        _identityClient = identityClient;
    }

    [BindProperty]
    public TrustDeviceInput Input { get; set; } = new();

    public string? ErrorMessage { get; set; }
    public bool HasChallengeContext { get; private set; } = true;

    public IActionResult OnGet()
    {
        var state = InteractiveLoginFlowStateStore.Read(TempData, keep: true);
        HasChallengeContext = state?.ChallengeId.HasValue == true;
        Input.TrustDevice = true;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var state = InteractiveLoginFlowStateStore.Read(TempData, keep: false);
        if (state == null || !state.ChallengeId.HasValue)
        {
            HasChallengeContext = false;
            return Page();
        }

        HasChallengeContext = true;
        if (Request.HasFormContentType &&
            Request.Form.TryGetValue("Input.TrustDevice", out var postedTrustDecision) &&
            bool.TryParse(postedTrustDecision.ToString(), out var parsedTrustDecision))
        {
            Input.TrustDevice = parsedTrustDecision;
        }

        var result = await _identityClient.CompleteTrustDeviceAsync(
            new CompleteTrustDeviceInput
            {
                Mode = state.Mode,
                TenantId = state.TenantId,
                ChallengeId = state.ChallengeId,
                TrustDevice = Input.TrustDevice
            },
            state.TenantId,
            HttpContext.RequestAborted);

        switch (result.Step)
        {
            case InteractiveLoginStep.Succeeded:
                {
                    var finalize = await FinalizeSignInAsync(state, HttpContext.RequestAborted);
                    if (finalize.Success && finalize.Result != null)
                    {
                        return finalize.Result;
                    }

                    ErrorMessage = finalize.Error ?? "Could not complete sign in.";
                    InteractiveLoginFlowStateStore.Save(TempData, state);
                    return Page();
                }
            case InteractiveLoginStep.RequiresMfa:
                state.ChallengeId = result.ChallengeId;
                state.ChallengeExpiresAtUtc = result.ChallengeExpiresAtUtc;
                InteractiveLoginFlowStateStore.Save(TempData, state);
                return Redirect(AuthPageRoutes.Mfa);
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
                ErrorMessage = result.Message ?? "Unable to complete trust-device flow.";
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

    public sealed class TrustDeviceInput
    {
        public bool TrustDevice { get; set; } = true;
    }
}
