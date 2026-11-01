using Auth.Clients;
using Auth.Routing;
using Auth.Pages.Account.Validation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Auth.Pages.Account;

[EnableRateLimiting("auth-reset")]
public class EmailVerificationResendModel : EmailVerificationPageModelBase
{
    public EmailVerificationResendModel(
        IIdentityClient identityClient,
        ITenantClient tenantClient,
        IAuthPageValidationService pageValidationService,
        IConfiguration configuration,
        IHostEnvironment environment)
        : base(identityClient, tenantClient, pageValidationService, configuration, environment)
    {
    }

    [BindProperty]
    public EmailVerificationInput Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? Tenant { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? LoginInput { get; set; }

    public void OnGet()
    {
        if (!string.IsNullOrWhiteSpace(Tenant))
        {
            Input.Tenant = AuthInputConstraints.NormalizeTenant(Tenant);
        }

        if (!string.IsNullOrWhiteSpace(LoginInput))
        {
            Input.LoginInput = AuthInputConstraints.NormalizeLoginInput(LoginInput);
        }
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Input.Tenant = AuthInputConstraints.NormalizeTenant(Input.Tenant);
        Input.LoginInput = AuthInputConstraints.NormalizeLoginInput(Input.LoginInput);

        ModelState.Clear();
        if (!await this.TryValidateInputAsync(PageValidationService, Input, nameof(Input), HttpContext.RequestAborted))
        {
            return Page();
        }

        await DispatchVerificationAsync(Input.Tenant, Input.LoginInput, IdentityClient.ResendEmailVerificationAsync);

        return Redirect(AuthPageRoutes.EmailVerificationSent);
    }
}
