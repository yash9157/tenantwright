using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Auth.Clients;
using Auth.Routing;
using OpenIddict.Abstractions;
using Shared.Security;

namespace Auth.Pages.Account;

public class LogoutModel : PageModel
{
    private readonly IIdentityClient _identityClient;
    private readonly ILogger<LogoutModel> _logger;

    public LogoutModel(
        IIdentityClient identityClient,
        ILogger<LogoutModel> logger)
    {
        _identityClient = identityClient;
        _logger = logger;
    }

    public IActionResult OnGet() => Page();

    public async Task<IActionResult> OnPostAsync()
    {
        try
        {
            await RevokeSessionIfExistsAsync();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Identity session revocation failed during interactive logout.");
        }

        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Redirect(AuthPageRoutes.SignIn);
    }

    private async Task RevokeSessionIfExistsAsync()
    {
        var sessionId = HttpContext.User.FindFirst(SharedClaimTypes.SessionId)?.Value;
        var subject = HttpContext.User.FindFirst(OpenIddictConstants.Claims.Subject)?.Value;
        Guid? tenantId = null;
        var tenantIdClaim = HttpContext.User.FindFirst(SharedClaimTypes.TenantId)?.Value;
        if (Guid.TryParse(tenantIdClaim, out var parsedTenantId))
        {
            tenantId = parsedTenantId;
        }

        if (Guid.TryParse(subject, out var userId) && !string.IsNullOrWhiteSpace(sessionId))
        {
            await _identityClient.RevokeSessionAsync(userId, sessionId, tenantId: tenantId, cancellationToken: HttpContext.RequestAborted);
        }
    }
}
