using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Auth.Clients;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using Shared.AuditLogs.Auditing;
using Shared.Security;

namespace Auth.Controllers;

[Route("~/connect/userinfo")]
public class UserInfoController : Controller
{
    private readonly IIdentityClient _identityClient;

    public UserInfoController(IIdentityClient identityClient)
    {
        _identityClient = identityClient;
    }

    [Authorize(AuthenticationSchemes = OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)]
    [HttpGet, HttpPost]
    [IgnoreAntiforgeryToken]
    [EnableRateLimiting("token")]
    [Produces("application/json")]
    public async Task<IActionResult> GetUserInfoAsync()
    {
        var principal = (await HttpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)).Principal;
        if (principal == null)
        {
            return Challenge(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        var subject = principal.GetClaim(OpenIddictConstants.Claims.Subject);
        if (!Guid.TryParse(subject, out var userId))
        {
            return Challenge(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        Guid? tenantId = null;
        var tenantIdClaim = principal.GetClaim(SharedClaimTypes.TenantId);
        if (!string.IsNullOrWhiteSpace(tenantIdClaim))
        {
            if (!Guid.TryParse(tenantIdClaim, out var parsedTenantId))
            {
                return Challenge(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
            }

            tenantId = parsedTenantId;
        }

        var userInfo = await _identityClient.GetUserInfoByIdAsync(
            userId,
            tenantId,
            strictAuthorization: true,
            bypassCache: false,
            cancellationToken: HttpContext.RequestAborted);
        if (userInfo == null || userInfo.Id != userId || userInfo.TenantId != tenantId || !userInfo.IsActive || userInfo.IsLockedOut)
        {
            return Challenge(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        var claims = new Dictionary<string, object>(System.StringComparer.Ordinal)
        {
            [OpenIddictConstants.Claims.Subject] = userInfo.Id.ToString()
        };

        if (principal.HasScope(OpenIddictConstants.Scopes.Email))
        {
            claims[OpenIddictConstants.Claims.Email] = userInfo.Email;
            claims[OpenIddictConstants.Claims.EmailVerified] = true;
        }

        if (principal.HasScope(OpenIddictConstants.Scopes.Phone) && !string.IsNullOrWhiteSpace(userInfo.PhoneNumber))
        {
            claims[OpenIddictConstants.Claims.PhoneNumber] = userInfo.PhoneNumber;
            claims[OpenIddictConstants.Claims.PhoneNumberVerified] = true;
        }

        if (principal.HasScope(OpenIddictConstants.Scopes.Profile))
        {
            claims[OpenIddictConstants.Claims.Name] = userInfo.UserName;
            claims[OpenIddictConstants.Claims.PreferredUsername] = userInfo.UserName;
        }

        if (principal.HasScope(OpenIddictConstants.Scopes.Roles))
        {
            claims[OpenIddictConstants.Claims.Role] = userInfo.Roles;
        }

        return Ok(claims);
    }
}
