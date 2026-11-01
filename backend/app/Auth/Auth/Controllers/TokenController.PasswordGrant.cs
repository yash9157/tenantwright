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
    private async Task<IActionResult> HandlePasswordGrantAsync(OpenIddictRequest request)
    {
        var tenantResolution = await ResolveTenantIdAsync(request);
        if (tenantResolution.Status == TenantResolutionStatus.NoneProvided)
        {
            await TryPublishAuditFailureAsync("tenant_required", request, tenantId: null, userId: null);
            return BuildOpenIddictForbid(OpenIddictConstants.Errors.InvalidGrant, GenericInvalidGrantDescription);
        }

        if (tenantResolution.Status != TenantResolutionStatus.Resolved || !tenantResolution.TenantId.HasValue)
        {
            await TryPublishAuditFailureAsync("tenant_invalid", request, tenantId: null, userId: null);
            return BuildOpenIddictForbid(OpenIddictConstants.Errors.InvalidGrant, GenericInvalidGrantDescription);
        }
        var resolvedTenantId = tenantResolution.TenantId.Value;

        var sessionId = Guid.NewGuid().ToString("N");
        var clientIpAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
        var browserInfo = Request.Headers.UserAgent.ToString();
        var twoFactorCode = request.GetParameter("two_factor_code")?.ToString();
        var twoFactorRecoveryCode = request.GetParameter("two_factor_recovery_code")?.ToString();
        // device_fingerprint is intentionally NOT accepted on the password grant.
        // Trusted-device registration and recognition are handled exclusively by the
        // interactive login flow (challenge guard + session binding). Accepting a raw
        // client-supplied fingerprint here would allow any caller who learns a fingerprint
        // to bypass MFA on the token endpoint without going through the interactive flow.
        if (string.IsNullOrWhiteSpace(browserInfo))
        {
            browserInfo = null;
        }

        var loginResult = await _identityClient.CheckPasswordAsync(
            request.Username!,
            request.Password!,
            tenantId: resolvedTenantId,
            sessionId: sessionId,
            clientId: request.ClientId,
            clientIpAddress: clientIpAddress,
            browserInfo: browserInfo,
            correlationId: HttpContext.TraceIdentifier,
            device: "Web",
            location: null,
            twoFactorCode: twoFactorCode,
            twoFactorRecoveryCode: twoFactorRecoveryCode,
            deviceFingerprint: null,
            rememberTrustedDevice: false,
            cancellationToken: HttpContext.RequestAborted);
        if (!loginResult.Succeeded)
        {
            await TryPublishAuditFailureAsync(
                ResolvePasswordGrantFailureReason(loginResult, twoFactorCode, twoFactorRecoveryCode),
                request,
                resolvedTenantId,
                null);

            return Forbid(
                authenticationSchemes: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
                properties: new AuthenticationProperties(new Dictionary<string, string?>
                {
                    [OpenIddictServerAspNetCoreConstants.Properties.Error] = OpenIddictConstants.Errors.InvalidGrant,
                    [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = GenericInvalidGrantDescription
                }));
        }

        var userInfo = await _identityClient.GetUserInfoAsync(request.Username!, tenantId: resolvedTenantId, cancellationToken: HttpContext.RequestAborted);
        if (userInfo == null)
        {
            await TryPublishAuditFailureAsync("user_info_missing", request, resolvedTenantId, null);
            return Forbid(
                authenticationSchemes: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
                properties: new AuthenticationProperties(new Dictionary<string, string?>
                {
                    [OpenIddictServerAspNetCoreConstants.Properties.Error] = OpenIddictConstants.Errors.InvalidGrant,
                    [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = GenericInvalidGrantDescription
                }));
        }

        if (!userInfo.IsActive || userInfo.IsLockedOut)
        {
            await TryPublishAuditFailureAsync("user_inactive_or_locked", request, resolvedTenantId, userInfo.Id);
            return Forbid(
                authenticationSchemes: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
                properties: new AuthenticationProperties(new Dictionary<string, string?>
                {
                    [OpenIddictServerAspNetCoreConstants.Properties.Error] = OpenIddictConstants.Errors.InvalidGrant,
                    [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = GenericInvalidGrantDescription
                }));
        }

        if (userInfo.TenantId != resolvedTenantId)
        {
            await TryPublishAuditFailureAsync("tenant_context_mismatch", request, resolvedTenantId, userInfo.Id);
            return Forbid(
                authenticationSchemes: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
                properties: new AuthenticationProperties(new Dictionary<string, string?>
                {
                    [OpenIddictServerAspNetCoreConstants.Properties.Error] = OpenIddictConstants.Errors.InvalidGrant,
                    [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = GenericInvalidGrantDescription
                }));
        }

        var identity = new ClaimsIdentity(
            authenticationType: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
            nameType: OpenIddictConstants.Claims.Name,
            roleType: OpenIddictConstants.Claims.Role);

        identity.AddClaim(OpenIddictConstants.Claims.Subject, userInfo.Id.ToString());
        identity.AddClaim(OpenIddictConstants.Claims.Name, userInfo.UserName);
        identity.AddClaim(OpenIddictConstants.Claims.Email, userInfo.Email);
        identity.AddClaim(SharedClaimTypes.SessionId, sessionId);

        if (userInfo.TenantId.HasValue)
        {
            identity.AddClaim(SharedClaimTypes.TenantId, userInfo.TenantId.Value.ToString());
        }

        if (!string.IsNullOrEmpty(userInfo.PhoneNumber))
        {
            identity.AddClaim(OpenIddictConstants.Claims.PhoneNumber, userInfo.PhoneNumber);
        }

        var principal = new ClaimsPrincipal(identity);
        principal.SetScopes(request.GetScopes());
        if (!_roleClaimsPrincipalService.TryApply(principal, userInfo, out var roleClaimsFailure))
        {
            await TryPublishAuditFailureAsync(
                roleClaimsFailure ?? "role_claim_construction_failed",
                request,
                resolvedTenantId,
                userInfo.Id);
            return BuildOpenIddictForbid(OpenIddictConstants.Errors.InvalidGrant, GenericInvalidGrantDescription);
        }
        var setPasswordResourcesFailure = await TrySetResourcesAsync(principal);
        if (setPasswordResourcesFailure != null)
        {
            return setPasswordResourcesFailure;
        }

        await _claimsPrincipalHandler.HandleAsync(principal);

        return SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }
}
