using System;
using System.Security.Claims;
using System.Globalization;
using Auth.Claims;
using Auth.Clients;
using Identity.Domain.Shared.Account;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using Shared.Caching.Sessions;
using Shared.Security;

namespace Auth.Pages.Account;

public abstract class AuthInteractivePageModelBase : PageModel
{
    private const string GenericFailureMessage = "We couldn't sign you in with those details.";

    private readonly IIdentityClient _identityClient;
    private readonly ITenantClient _tenantClient;
    private readonly IOpenIddictClaimsPrincipalHandler _claimsPrincipalHandler;
    private readonly IOpenIddictApplicationManager _applicationManager;

    protected AuthInteractivePageModelBase(
        IIdentityClient identityClient,
        ITenantClient tenantClient,
        IOpenIddictClaimsPrincipalHandler claimsPrincipalHandler,
        IOpenIddictApplicationManager applicationManager)
    {
        _identityClient = identityClient;
        _tenantClient = tenantClient;
        _claimsPrincipalHandler = claimsPrincipalHandler;
        _applicationManager = applicationManager;
    }

    protected async Task<FinalizeSignInResult> FinalizeSignInAsync(
        InteractiveLoginFlowState flowState,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidFlowState(flowState))
        {
            return FinalizeSignInResult.Failed(GenericFailureMessage);
        }

        if (flowState.Mode == InteractiveLoginMode.Tenant)
        {
            var tenant = await _tenantClient.FindByIdAsync(flowState.TenantId!.Value, cancellationToken);
            if (tenant == null || !tenant.IsActive)
            {
                return FinalizeSignInResult.Failed(GenericFailureMessage);
            }
        }

        var userInfo = await _identityClient.GetUserInfoAsync(
            flowState.LoginInput,
            flowState.TenantId,
            flowState.Mode,
            cancellationToken);
        if (userInfo == null)
        {
            return FinalizeSignInResult.Failed(GenericFailureMessage);
        }

        if (!userInfo.IsActive || userInfo.IsLockedOut)
        {
            return FinalizeSignInResult.Failed(GenericFailureMessage);
        }

        if (userInfo.TenantId != flowState.TenantId)
        {
            return FinalizeSignInResult.Failed(GenericFailureMessage);
        }

        var claims = new List<Claim>
        {
            new(OpenIddictConstants.Claims.Subject, userInfo.Id.ToString()),
            new(OpenIddictConstants.Claims.Name, userInfo.UserName),
            new(OpenIddictConstants.Claims.Email, userInfo.Email),
            new(SharedClaimTypes.SessionId, flowState.SessionId)
        };

        var authenticationMethod = string.IsNullOrWhiteSpace(flowState.AuthenticationMethod)
            ? "pwd"
            : flowState.AuthenticationMethod.Trim().ToLowerInvariant();
        claims.Add(new Claim(OpenIddictConstants.Claims.AuthenticationMethodReference, authenticationMethod));
        var authenticatedAtUtc = flowState.AuthenticatedAtUtc ?? DateTimeOffset.UtcNow;
        claims.Add(new Claim(OpenIddictConstants.Claims.AuthenticationTime,
            authenticatedAtUtc.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));

        if (userInfo.TenantId.HasValue)
        {
            claims.Add(new Claim(SharedClaimTypes.TenantId, userInfo.TenantId.Value.ToString()));
        }

        if (!string.IsNullOrWhiteSpace(userInfo.PhoneNumber))
        {
            claims.Add(new Claim(OpenIddictConstants.Claims.PhoneNumber, userInfo.PhoneNumber));
        }

        foreach (var role in userInfo.Roles)
        {
            claims.Add(new Claim(OpenIddictConstants.Claims.Role, role));
        }

        var claimsIdentity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var claimsPrincipal = new ClaimsPrincipal(claimsIdentity);

        await _claimsPrincipalHandler.HandleAsync(claimsPrincipal);

        var authProperties = new AuthenticationProperties
        {
            IsPersistent = flowState.RememberMe
        };
        if (flowState.RememberMe)
        {
            var sessionValidationOptions = HttpContext.RequestServices
                .GetRequiredService<IOptions<SessionValidationOptions>>()
                .Value;
            var markerTtlHours = Math.Max(1, sessionValidationOptions.MarkerTtlHours);
            authProperties.ExpiresUtc = DateTimeOffset.UtcNow.AddHours(markerTtlHours);
        }

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            claimsPrincipal,
            authProperties);

        if (!string.IsNullOrWhiteSpace(flowState.ReturnUrl) && await IsSafeReturnUrl(flowState.ReturnUrl))
        {
            return FinalizeSignInResult.Succeeded(new RedirectResult(flowState.ReturnUrl));
        }

        return FinalizeSignInResult.Succeeded(new RedirectToPageResult("/Index"));
    }

    private static bool IsValidFlowState(InteractiveLoginFlowState flowState)
    {
        return flowState.Mode switch
        {
            InteractiveLoginMode.Tenant => flowState.TenantId.HasValue && flowState.TenantId.Value != Guid.Empty,
            InteractiveLoginMode.Platform => !flowState.TenantId.HasValue,
            _ => false
        };
    }

    protected string? GetClientIdFromReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl))
        {
            return null;
        }

        if (!returnUrl.StartsWith("/connect/authorize", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var queryIndex = returnUrl.IndexOf('?', StringComparison.Ordinal);
        if (queryIndex < 0)
        {
            return null;
        }

        var queryString = returnUrl[queryIndex..];
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(queryString);
        return query.TryGetValue("client_id", out var value) ? value.ToString() : null;
    }

    protected async Task<bool> IsSafeReturnUrl(string returnUrl)
    {
        if (string.IsNullOrEmpty(returnUrl))
        {
            return false;
        }

        if (Url.IsLocalUrl(returnUrl))
        {
            return true;
        }

        if (!Uri.TryCreate(returnUrl, UriKind.Absolute, out var returnUri))
        {
            return false;
        }

        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(returnUri.Query);
        if (query.TryGetValue("client_id", out var clientId))
        {
            var client = await _applicationManager.FindByClientIdAsync(clientId!);
            if (client != null)
            {
                var redirectUris = await _applicationManager.GetRedirectUrisAsync(client);
                return redirectUris.Any(x => IsAllowedRedirectUri(returnUri, x));
            }
        }

        return false;
    }

    private static bool IsAllowedRedirectUri(Uri returnUri, string allowedRedirectUri)
    {
        if (!Uri.TryCreate(allowedRedirectUri, UriKind.Absolute, out var allowedUri))
        {
            return false;
        }

        if (!string.Equals(returnUri.Scheme, allowedUri.Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(returnUri.Host, allowedUri.Host, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (returnUri.Port != allowedUri.Port)
        {
            return false;
        }

        if (!string.Equals(returnUri.AbsolutePath, allowedUri.AbsolutePath, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(allowedUri.Query) &&
            !string.Equals(returnUri.Query, allowedUri.Query, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    protected readonly record struct FinalizeSignInResult(bool Success, string? Error, IActionResult? Result)
    {
        public static FinalizeSignInResult Failed(string error)
            => new(false, error, null);

        public static FinalizeSignInResult Succeeded(IActionResult result)
            => new(true, null, result);
    }
}
