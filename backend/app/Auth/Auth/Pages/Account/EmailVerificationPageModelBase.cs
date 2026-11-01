using System.ComponentModel.DataAnnotations;
using Auth.Clients;
using Auth.Pages.Account.Validation;
using Identity.Application.Contracts.Account;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Auth.Pages.Account;

/// <summary>
/// Shared base for the email-verification request and resend pages, which differ only
/// in which identity-client call they make. Keeps tenant resolution, base-URL safety,
/// and the request payload identical across both flows.
/// </summary>
public abstract class EmailVerificationPageModelBase : PageModel
{
    protected IIdentityClient IdentityClient { get; }
    private readonly ITenantClient _tenantClient;
    protected IAuthPageValidationService PageValidationService { get; }
    private readonly string? _publicBaseUrl;
    private readonly bool _isDevelopment;

    protected EmailVerificationPageModelBase(
        IIdentityClient identityClient,
        ITenantClient tenantClient,
        IAuthPageValidationService pageValidationService,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        IdentityClient = identityClient;
        _tenantClient = tenantClient;
        PageValidationService = pageValidationService;
        _publicBaseUrl = configuration["Auth:PublicBaseUrl"];
        _isDevelopment = environment.IsDevelopment();
    }

    /// <summary>
    /// Resolve the active tenant and dispatch a verification email via <paramref name="send"/>.
    /// Always fail-closed and enumeration-safe: silently no-ops when the tenant is missing,
    /// inactive, or no safe base URL is available — the caller still redirects to the generic
    /// "sent" page regardless.
    /// </summary>
    protected async Task DispatchVerificationAsync(
        string tenant,
        string loginInput,
        Func<EmailVerificationRequestInput, Guid, CancellationToken, Task> send)
    {
        var tenantNormalizedName = tenant.Trim().ToUpperInvariant();
        var resolvedTenant = await _tenantClient.FindByNormalizedNameAsync(tenantNormalizedName, HttpContext.RequestAborted);
        var verificationBaseUrl = ResolveVerificationBaseUrl();

        if (resolvedTenant?.IsActive == true && !string.IsNullOrWhiteSpace(verificationBaseUrl))
        {
            await send(
                new EmailVerificationRequestInput
                {
                    TenantId = resolvedTenant.Id,
                    LoginInput = loginInput.Trim(),
                    VerificationBaseUrl = verificationBaseUrl,
                    ClientIpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
                    BrowserInfo = Request.Headers.UserAgent.ToString(),
                    CorrelationId = HttpContext.TraceIdentifier
                },
                resolvedTenant.Id,
                HttpContext.RequestAborted);
        }
    }

    private string? ResolveVerificationBaseUrl()
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

    /// <summary>Shared input shape for both verification pages.</summary>
    public sealed class EmailVerificationInput
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
