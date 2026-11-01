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
    private async Task<IActionResult> HandleClientCredentialsGrantAsync(OpenIddictRequest request)
    {
        var tenantResolution = await ResolveTenantIdAsync(request);
        if (tenantResolution.Status == TenantResolutionStatus.Invalid)
        {
            await TryPublishAuditFailureAsync("client_credentials_tenant_invalid", request, tenantId: null, userId: null);
            return BuildOpenIddictForbid(OpenIddictConstants.Errors.InvalidGrant, "Tenant is invalid or inactive.");
        }

        var resolvedTenantId = tenantResolution.TenantId;

        var identity = new ClaimsIdentity(
            authenticationType: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
            nameType: OpenIddictConstants.Claims.Name,
            roleType: OpenIddictConstants.Claims.Role);

        if (!string.IsNullOrWhiteSpace(request.ClientId))
        {
            identity.AddClaim(OpenIddictConstants.Claims.Subject, request.ClientId);
            identity.AddClaim(OpenIddictConstants.Claims.ClientId, request.ClientId);
            identity.AddClaim(OpenIddictConstants.Claims.Name, request.ClientId);
        }

        if (resolvedTenantId.HasValue)
        {
            identity.AddClaim(SharedClaimTypes.TenantId, resolvedTenantId.Value.ToString());
        }

        var principal = new ClaimsPrincipal(identity);
        principal.SetScopes(request.GetScopes());
        var setClientResourcesFailure = await TrySetResourcesAsync(principal);
        if (setClientResourcesFailure != null)
        {
            return setClientResourcesFailure;
        }

        await _claimsPrincipalHandler.HandleAsync(principal);

        return SignIn(principal, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }
}
