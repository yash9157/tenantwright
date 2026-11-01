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
public class PasswordExpiredModel : AuthInteractivePageModelBase
{
    private readonly IIdentityClient _identityClient;
    private readonly IAuthPageValidationService _pageValidationService;

    public PasswordExpiredModel(
        IIdentityClient identityClient,
        ITenantClient tenantClient,
        IAuthPageValidationService pageValidationService,
        IAuthPasswordPolicyProvider passwordPolicyProvider,
        IOpenIddictClaimsPrincipalHandler claimsPrincipalHandler,
        IOpenIddictApplicationManager applicationManager)
        : base(identityClient, tenantClient, claimsPrincipalHandler, applicationManager)
    {
        _identityClient = identityClient;
        _pageValidationService = pageValidationService;
        PasswordPolicy = passwordPolicyProvider.GetPolicy();
    }

    [BindProperty]
    public PasswordExpiredInput Input { get; set; } = new();

    public string? ErrorMessage { get; set; }
    public bool HasChallengeContext { get; private set; } = true;
    public AuthPasswordPolicyDescriptor PasswordPolicy { get; }

    public IActionResult OnGet()
    {
        var state = InteractiveLoginFlowStateStore.Read(TempData, keep: true);
        HasChallengeContext = state?.ChallengeId.HasValue == true;
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
        Input.CurrentPassword = AuthInputConstraints.NormalizePassword(Input.CurrentPassword);
        Input.NewPassword = AuthInputConstraints.NormalizePassword(Input.NewPassword);
        Input.ConfirmPassword = AuthInputConstraints.NormalizePassword(Input.ConfirmPassword);

        ModelState.Clear();
        if (!await this.TryValidateInputAsync(_pageValidationService, Input, nameof(Input), HttpContext.RequestAborted))
        {
            InteractiveLoginFlowStateStore.Save(TempData, state);
            return Page();
        }

        var changeResult = await _identityClient.ChangeExpiredPasswordAsync(
            new PasswordExpiredChangeInput
            {
                Mode = state.Mode,
                TenantId = state.TenantId,
                ChallengeId = state.ChallengeId,
                CurrentPassword = Input.CurrentPassword,
                NewPassword = Input.NewPassword,
                ConfirmPassword = Input.ConfirmPassword
            },
            state.TenantId,
            HttpContext.RequestAborted);

        if (!changeResult.Succeeded)
        {
            ErrorMessage = changeResult.Errors.Count > 0
                ? string.Join(Environment.NewLine, changeResult.Errors)
                : "Could not change password.";
            InteractiveLoginFlowStateStore.Save(TempData, state);
            return Page();
        }

        PasswordChangedSuccessStateStore.Save(
            TempData,
            new PasswordChangedSuccessState
            {
                Mode = state.Mode,
                ReturnUrl = state.ReturnUrl,
                Tenant = state.Tenant,
                LoginInput = state.LoginInput
            });

        return Redirect(AuthPageRoutes.PasswordChanged);
    }

    public sealed class PasswordExpiredInput
    {
        [Required]
        [DataType(DataType.Password)]
        [StringLength(AuthInputConstraints.PasswordMaxLength)]
        public string CurrentPassword { get; set; } = string.Empty;

        [Required]
        [DataType(DataType.Password)]
        [StringLength(AuthInputConstraints.PasswordMaxLength)]
        public string NewPassword { get; set; } = string.Empty;

        [Required]
        [DataType(DataType.Password)]
        [StringLength(AuthInputConstraints.PasswordMaxLength)]
        public string ConfirmPassword { get; set; } = string.Empty;
    }
}
