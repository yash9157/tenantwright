using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Security.Claims;
using System.Threading.Tasks;
using Auth.Claims;
using Auth.Authorization;
using Auth.Clients;
using Auth.Routing;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using Shared.AuditLogs.Auditing;
using Shared.Security;

namespace Auth.Controllers;

public class AuthorizationController : Controller
{
    private readonly IOpenIddictClaimsPrincipalHandler _claimsPrincipalHandler;
    private readonly IOpenIddictScopeManager _scopeManager;
    private readonly IIdentityClient _identityClient;
    private readonly IAntiforgery _antiforgery;
    private readonly IOpenIddictApplicationManager _applicationManager;
    private readonly RoleClaimsPrincipalService _roleClaimsPrincipalService;
    private readonly ILogger<AuthorizationController> _logger;

    public AuthorizationController(
        IOpenIddictClaimsPrincipalHandler claimsPrincipalHandler,
        IOpenIddictScopeManager scopeManager,
        IIdentityClient identityClient,
        IAntiforgery antiforgery,
        IOpenIddictApplicationManager applicationManager,
        RoleClaimsPrincipalService roleClaimsPrincipalService,
        ILogger<AuthorizationController> logger)
    {
        _claimsPrincipalHandler = claimsPrincipalHandler;
        _scopeManager = scopeManager;
        _identityClient = identityClient;
        _antiforgery = antiforgery;
        _applicationManager = applicationManager;
        _roleClaimsPrincipalService = roleClaimsPrincipalService;
        _logger = logger;
    }

    [HttpGet("~/connect/authorize")]
    [HttpPost("~/connect/authorize")]
    [IgnoreAntiforgeryToken]
    [AllowAnonymous]
    [EnableRateLimiting("token")]
    public async Task<IActionResult> AuthorizeAsync()
    {
        var request = HttpContext.GetOpenIddictServerRequest() ??
            throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

        var application = string.IsNullOrWhiteSpace(request.ClientId)
            ? null
            : await _applicationManager.FindByClientIdAsync(request.ClientId);
        var consentType = application == null
            ? null
            : await _applicationManager.GetConsentTypeAsync(application);
        if (application == null || !TrustedFirstPartyConsentPolicy.IsSupported(consentType))
        {
            return Forbid(
                authenticationSchemes: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
                properties: new AuthenticationProperties(new Dictionary<string, string?>
                {
                    [OpenIddictServerAspNetCoreConstants.Properties.Error] = OpenIddictConstants.Errors.InvalidRequest,
                    [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] =
                        "The client consent configuration is not supported."
                }));
        }

        // Retrieve the user principal stored in the authentication cookie.
        var result = await HttpContext.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        // If the user principal is not available, redirect to the login page.
        if (!result.Succeeded)
        {
            return Challenge(
                authenticationSchemes: CookieAuthenticationDefaults.AuthenticationScheme,
                properties: new AuthenticationProperties
                {
                    RedirectUri = Request.PathBase + Request.Path + QueryString.Create(
                        Request.HasFormContentType ? Request.Form.ToImmutableDictionary() : Request.Query.ToImmutableDictionary())
                });
        }

        var subjectValue = result.Principal.FindFirst(OpenIddictConstants.Claims.Subject)?.Value
                           ?? result.Principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(subjectValue, out var userId))
        {
            return Forbid(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        Guid? tenantId = null;
        var tenantValue = result.Principal.FindFirst(SharedClaimTypes.TenantId)?.Value;
        if (!string.IsNullOrWhiteSpace(tenantValue))
        {
            if (!Guid.TryParse(tenantValue, out var parsedTenantId))
            {
                return Forbid(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
            }

            tenantId = parsedTenantId;
        }

        var userInfo = await _identityClient.GetUserInfoByIdAsync(
            userId,
            tenantId,
            strictAuthorization: true,
            bypassCache: false,
            cancellationToken: HttpContext.RequestAborted);
        if (userInfo == null)
        {
            return Forbid(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        // Build from the current strict Identity authorization snapshot rather than stale cookie roles.
        var identity = new ClaimsIdentity(
            authenticationType: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
            nameType: OpenIddictConstants.Claims.Name,
            roleType: OpenIddictConstants.Claims.Role);

        identity.AddClaim(OpenIddictConstants.Claims.Subject, userInfo.Id.ToString("D"));
        identity.AddClaim(OpenIddictConstants.Claims.Name, userInfo.UserName);
        if (!string.IsNullOrWhiteSpace(userInfo.Email))
        {
            identity.AddClaim(OpenIddictConstants.Claims.Email, userInfo.Email);
        }
        if (!string.IsNullOrWhiteSpace(userInfo.PhoneNumber))
        {
            identity.AddClaim(OpenIddictConstants.Claims.PhoneNumber, userInfo.PhoneNumber);
        }
        if (userInfo.TenantId.HasValue)
        {
            identity.AddClaim(SharedClaimTypes.TenantId, userInfo.TenantId.Value.ToString("D"));
        }
        var sessionId = result.Principal.FindFirst(SharedClaimTypes.SessionId)?.Value;
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            identity.AddClaim(SharedClaimTypes.SessionId, sessionId);
        }

        var principal = new ClaimsPrincipal(identity);

        // Set the list of scopes granted to the client application.
        principal.SetScopes(request.GetScopes());
        if (!_roleClaimsPrincipalService.TryApply(principal, userInfo, out _))
        {
            return Forbid(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }
        principal.SetResources(await GetResourcesAsync(principal.GetScopes()));

        await _claimsPrincipalHandler.HandleAsync(principal);

        return SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    [HttpGet("~/connect/logout")]
    [AllowAnonymous]
    public async Task<IActionResult> LogoutAsync()
    {
        var request = HttpContext.GetOpenIddictServerRequest() ??
            throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");
        var identityTokenHintPrincipal =
            (await HttpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)).Principal;

        if (LogoutRequestPolicy.CanCompleteWithoutConfirmation(
                HttpContext.User,
                identityTokenHintPrincipal,
                request))
        {
            return await CompleteLogoutAsync(request, identityTokenHintPrincipal);
        }

        var tokens = _antiforgery.GetAndStoreTokens(HttpContext);
        var action = "/connect/logout" + Request.QueryString;

        var html =
            "<!doctype html><html><head><meta charset=\"utf-8\" />" +
            "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\" />" +
            "<title>Sign out</title></head><body>" +
            "<form method=\"post\" action=\"" + System.Net.WebUtility.HtmlEncode(action) + "\">" +
            "<input type=\"hidden\" name=\"" + System.Net.WebUtility.HtmlEncode(tokens.FormFieldName) + "\" value=\"" + System.Net.WebUtility.HtmlEncode(tokens.RequestToken) + "\" />" +
            "<button type=\"submit\">Sign out</button>" +
            "</form></body></html>";

        return Content(html, "text/html");
    }

    [HttpPost("~/connect/logout")]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> LogoutPostAsync()
    {
        var request = HttpContext.GetOpenIddictServerRequest() ??
            throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");
        var identityTokenHintPrincipal =
            (await HttpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)).Principal;

        return await CompleteLogoutAsync(request, identityTokenHintPrincipal);
    }

    private async Task<IActionResult> CompleteLogoutAsync(
        OpenIddictRequest request,
        ClaimsPrincipal? identityTokenHintPrincipal)
    {
        var sessionPrincipal = LogoutRequestPolicy.GetSessionPrincipal(
            HttpContext.User,
            identityTokenHintPrincipal);
        var sessionId = sessionPrincipal?.FindFirst(SharedClaimTypes.SessionId)?.Value;
        var subject = sessionPrincipal?.FindFirst(OpenIddictConstants.Claims.Subject)?.Value;
        Guid? tenantId = null;
        var tenantIdClaim = sessionPrincipal?.FindFirst(SharedClaimTypes.TenantId)?.Value;
        if (Guid.TryParse(tenantIdClaim, out var parsedTenantId))
        {
            tenantId = parsedTenantId;
        }
        if (Guid.TryParse(subject, out var userId) && !string.IsNullOrWhiteSpace(sessionId))
        {
            try
            {
                await _identityClient.RevokeSessionAsync(
                    userId,
                    sessionId,
                    tenantId: tenantId,
                    cancellationToken: HttpContext.RequestAborted);
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Identity session persistence failed during logout. The shared session marker was removed. UserId={UserId}",
                    userId);
            }
        }

        // Local sign-out must complete even when the idempotent Identity write
        // is temporarily unavailable. Shared marker removal already makes API
        // validation fail closed.
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        // OpenIddict has already validated the registered post-logout URI
        // before endpoint passthrough. Use that exact callback; a manual
        // end-session request without a client callback returns to sign-in
        // instead of an empty response.
        return SignOut(
            authenticationSchemes: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
            properties: new AuthenticationProperties
            {
                RedirectUri = LogoutRequestPolicy.GetCompletionRedirectUri(
                    request,
                    AuthPageRoutes.SignIn)
            });
    }

    private async Task<IEnumerable<string>> GetResourcesAsync(ImmutableArray<string> scopes)
    {
        var resources = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var resource in _scopeManager.ListResourcesAsync(scopes))
        {
            resources.Add(resource);
        }

        return resources;
    }
}
