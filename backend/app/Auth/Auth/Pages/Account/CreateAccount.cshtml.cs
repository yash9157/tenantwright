using System.ComponentModel.DataAnnotations;
using Auth.Pages.Account.Validation;
using Auth.Registration;
using Auth.Routing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

namespace Auth.Pages.Account;

[EnableRateLimiting("auth-login")]
public class CreateAccountModel : PageModel
{
    private readonly IRegistrationProvisioningService _registrationProvisioningService;
    private readonly IPublicTenantRegistrationGate _registrationGate;
    private readonly PublicTenantRegistrationOptions _registrationOptions;
    private readonly IAuthPageValidationService _pageValidationService;

    public CreateAccountModel(
        IRegistrationProvisioningService registrationProvisioningService,
        IPublicTenantRegistrationGate registrationGate,
        IAuthPageValidationService pageValidationService,
        IOptions<PublicTenantRegistrationOptions> registrationOptions)
    {
        _registrationProvisioningService = registrationProvisioningService;
        _registrationGate = registrationGate;
        _registrationOptions = registrationOptions.Value;
        _pageValidationService = pageValidationService;
    }

    [BindProperty]
    public CreateAccountInput Input { get; set; } = new();

    public string? ErrorMessage { get; private set; }

    public AuthPasswordPolicyDescriptor PasswordPolicy { get; private set; } = new();
    public bool PublicRegistrationEnabled => _registrationOptions.Enabled;
    public string TurnstileSiteKey => _registrationOptions.TurnstileSiteKey;

    public IActionResult OnGet([FromServices] IAuthPasswordPolicyProvider passwordPolicyProvider)
    {
        PasswordPolicy = passwordPolicyProvider.GetPolicy();
        Input.IdempotencyToken = Guid.NewGuid().ToString("N");
        return Page();
    }

    public async Task<IActionResult> OnPostAsync([FromServices] IAuthPasswordPolicyProvider passwordPolicyProvider)
    {
        PasswordPolicy = passwordPolicyProvider.GetPolicy();

        Input.Tenant = AuthInputConstraints.NormalizeTenant(Input.Tenant);
        Input.UserName = AuthInputConstraints.NormalizeRequired(Input.UserName, AuthInputConstraints.LoginInputMaxLength);
        Input.Email = AuthInputConstraints.NormalizeRequired(Input.Email, AuthInputConstraints.LoginInputMaxLength);
        Input.IdempotencyToken = AuthInputConstraints.NormalizeRequired(Input.IdempotencyToken, 32);
        Input.CaptchaToken = AuthInputConstraints.NormalizeRequired(Input.CaptchaToken, 4096);

        ModelState.Clear();
        if (!await this.TryValidateInputAsync(_pageValidationService, Input, nameof(Input), HttpContext.RequestAborted))
        {
            return Page();
        }

        if (!await _registrationGate.IsAllowedAsync(Input.CaptchaToken, Input.Email, Input.Tenant, HttpContext.Connection.RemoteIpAddress?.ToString(), HttpContext.RequestAborted))
        {
            ErrorMessage = "We couldn't start your organization. Check your details and try again.";
            return Page();
        }

        var result = await _registrationProvisioningService.RegisterAsync(
            new RegistrationProvisioningRequest
            {
                IdempotencyToken = Input.IdempotencyToken,
                Tenant = Input.Tenant,
                UserName = Input.UserName,
                Email = Input.Email
            },
            HttpContext.RequestAborted);

        if (!result.Succeeded)
        {
            // Use a single generic message regardless of the specific failure reason
            // (tenant already exists, provisioning partial failure, rollback issue)
            // to prevent enumeration of existing tenants or internal provisioning state.
            // Specific failure details are logged server-side by RegistrationProvisioningService.
            ErrorMessage = "We couldn't create your account. Check your details and try again.";
            return Page();
        }

        return Redirect($"{AuthPageRoutes.PublicRegistrationStatus}?token={Uri.EscapeDataString(result.StatusToken)}");
    }

    public sealed class CreateAccountInput
    {
        [Required]
        [StringLength(32, MinimumLength = 32)]
        [RegularExpression("^[a-fA-F0-9]{32}$")]
        public string IdempotencyToken { get; set; } = string.Empty;

        [Required]
        [StringLength(4096)]
        public string CaptchaToken { get; set; } = string.Empty;

        [Required]
        [StringLength(AuthInputConstraints.TenantMaxLength, MinimumLength = 2)]
        [RegularExpression(@"^[a-zA-Z0-9._-]+$", ErrorMessage = "Enter a valid Organization ID.")]
        [Display(Name = "Tenant ID")]
        public string Tenant { get; set; } = string.Empty;

        [Required]
        [StringLength(AuthInputConstraints.LoginInputMaxLength, MinimumLength = 3)]
        [Display(Name = "Administrator Username")]
        public string UserName { get; set; } = string.Empty;

        [Required]
        [EmailAddress]
        [StringLength(AuthInputConstraints.LoginInputMaxLength)]
        [Display(Name = "Work Email")]
        public string Email { get; set; } = string.Empty;

    }
}
