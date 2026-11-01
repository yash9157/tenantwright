using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Auth.Clients;
using Auth.Claims;
using Auth.TokenMetadata;
using Identity.Application.Contracts.Account;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using Shared.AuditLogs.Auditing;
using Shared.AuditLogs.Publishing;
using Shared.Security;

namespace Auth.Controllers;

public partial class TokenController
{
    private async Task<IActionResult> HandleAuthorizationCodeAndRefreshGrantAsync(OpenIddictRequest request)
    {
        // Retrieve the claims principal stored in the authorization code/refresh token.
        var principal = (await HttpContext.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme)).Principal;
        if (principal is not ClaimsPrincipal nonNullPrincipal)
        {
            return Forbid(
                authenticationSchemes: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
                properties: new AuthenticationProperties(new Dictionary<string, string?>
                {
                    [OpenIddictServerAspNetCoreConstants.Properties.Error] = OpenIddictConstants.Errors.InvalidGrant,
                    [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = "The token is no longer valid."
                }));
        }

        if (nonNullPrincipal.GetResources().IsDefaultOrEmpty)
        {
            var setRefreshResourcesFailure = await TrySetResourcesAsync(nonNullPrincipal);
            if (setRefreshResourcesFailure != null)
            {
                return setRefreshResourcesFailure;
            }
        }

        if (request.IsRefreshTokenGrantType())
        {
            var sessionId = nonNullPrincipal.FindFirst(SharedClaimTypes.SessionId)?.Value;
            var subject = nonNullPrincipal.FindFirst(OpenIddictConstants.Claims.Subject)?.Value;
            if (!Guid.TryParse(subject, out var userId) || string.IsNullOrWhiteSpace(sessionId))
            {
                await TryPublishAuditFailureAsync("refresh_session_missing", request, tenantId: null, userId: null);
                return Forbid(
                    authenticationSchemes: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
                    properties: new AuthenticationProperties(new Dictionary<string, string?>
                    {
                        [OpenIddictServerAspNetCoreConstants.Properties.Error] = OpenIddictConstants.Errors.InvalidGrant,
                        [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = "Session information is missing."
                    }));
            }

            Guid? tenantId = null;
            var tenantIdClaim = nonNullPrincipal.FindFirst(SharedClaimTypes.TenantId)?.Value;
            if (Guid.TryParse(tenantIdClaim, out var parsedTenantId))
            {
                tenantId = parsedTenantId;
            }

            var isValid = await _identityClient.ValidateSessionAsync(userId, sessionId, tenantId: tenantId, cancellationToken: HttpContext.RequestAborted);
            if (!isValid)
            {
                await TryPublishAuditFailureAsync("refresh_session_revoked", request, tenantId: tenantId, userId: userId);
                return Forbid(
                    authenticationSchemes: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
                    properties: new AuthenticationProperties(new Dictionary<string, string?>
                    {
                        [OpenIddictServerAspNetCoreConstants.Properties.Error] = OpenIddictConstants.Errors.InvalidGrant,
                        [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = "Session revoked."
                    }));
            }

            var refreshedPrincipal = await TryRefreshUserClaimsAsync(nonNullPrincipal, userId, tenantId, HttpContext.RequestAborted);
            if (refreshedPrincipal == null)
            {
                await TryPublishAuditFailureAsync("refresh_claim_rebuild_failed", request, tenantId: tenantId, userId: userId);
                return Forbid(
                    authenticationSchemes: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
                    properties: new AuthenticationProperties(new Dictionary<string, string?>
                    {
                        [OpenIddictServerAspNetCoreConstants.Properties.Error] = OpenIddictConstants.Errors.InvalidGrant,
                        [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = "User information could not be refreshed."
                    }));
            }

            nonNullPrincipal = refreshedPrincipal;
        }

        return SignIn(nonNullPrincipal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }
}
