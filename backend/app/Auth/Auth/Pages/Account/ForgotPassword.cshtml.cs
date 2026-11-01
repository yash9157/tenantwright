using System.ComponentModel.DataAnnotations;
using Auth.Clients;
using Auth.Routing;
using Auth.Pages.Account.Validation;
using Identity.Application.Contracts.Account;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Auth.Pages.Account;

[EnableRateLimiting("auth-reset")]
public class ForgotPasswordModel : PageModel
{
    private readonly IIdentityClient _identityClient;
    private readonly ITenantClient _tenantClient;
    private readonly IAuthPageValidationService _pageValidationService;
    private readonly string? _publicBaseUrl;
    private readonly bool _isDevelopment;

    public ForgotPasswordModel(
        IIdentityClient identityClient,
        ITenantClient tenantClient,
        IAuthPageValidationService pageValidationService,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        _identityClient = identityClient;
        _tenantClient = tenantClient;
        _pageValidationService = pageValidationService;
        _publicBaseUrl = configuration["Auth:PublicBaseUrl"];
        _isDevelopment = environment.IsDevelopment();
    }

    [BindProperty]
    public ForgotPasswordInput Input { get; set; } = new();

    public string? ErrorMessage { get; set; }

    public IActionResult OnGet()
    {
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Input.Tenant = AuthInputConstraints.NormalizeTenant(Input.Tenant);
        Input.LoginInput = AuthInputConstraints.NormalizeLoginInput(Input.LoginInput);

        ModelState.Clear();
        if (!await this.TryValidateInputAsync(_pageValidationService, Input, nameof(Input), HttpContext.RequestAborted))
        {
            return Page();
        }

        var tenantNormalizedName = Input.Tenant.Trim().ToUpperInvariant();
        var tenant = await _tenantClient.FindByNormalizedNameAsync(tenantNormalizedName, HttpContext.RequestAborted);
        var resetBaseUrl = ResolveResetBaseUrl();
        if (tenant?.IsActive == true && !string.IsNullOrWhiteSpace(resetBaseUrl))
        {
            await _identityClient.RequestPasswordResetAsync(
                new PasswordResetRequestInput
                {
                    TenantId = tenant.Id,
                    LoginInput = Input.LoginInput.Trim(),
                    ResetBaseUrl = resetBaseUrl,
                    ClientIpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
                    BrowserInfo = Request.Headers.UserAgent.ToString(),
                    CorrelationId = HttpContext.TraceIdentifier
                },
                tenant.Id,
                HttpContext.RequestAborted);
        }

        return Redirect(AuthPageRoutes.ForgotPasswordSent);
    }

    private string? ResolveResetBaseUrl()
    {
        var configured = _publicBaseUrl?.Trim().TrimEnd('/');
        if (Uri.TryCreate(configured, UriKind.Absolute, out var configuredUri) &&
            (string.Equals(configuredUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
             (_isDevelopment && string.Equals(configuredUri.Host, "localhost", StringComparison.OrdinalIgnoreCase))))
        {
            return configured;
        }

        if (_isDevelopment)
        {
            return $"{Request.Scheme}://{Request.Host}";
        }

        return null;
    }

    public sealed class ForgotPasswordInput
    {
        [Required]
        [StringLength(AuthInputConstraints.TenantMaxLength, MinimumLength = 2)]
        [RegularExpression(@"^[a-zA-Z0-9._-]+$", ErrorMessage = "Enter a valid Organization ID.")]
        public string Tenant { get; set; } = string.Empty;

        [Required]
        [StringLength(AuthInputConstraints.LoginInputMaxLength, MinimumLength = 3)]
        [EmailOrUsername]
        [Display(Name = "Email or Username")]
        public string LoginInput { get; set; } = string.Empty;
    }
}
