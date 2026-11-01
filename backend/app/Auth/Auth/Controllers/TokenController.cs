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

public partial class TokenController : Controller
{
    private const string GenericInvalidGrantDescription = "Invalid credentials or tenant.";

    private enum TenantResolutionStatus
    {
        NoneProvided = 0,
        Resolved = 1,
        Invalid = 2
    }

    private readonly IIdentityClient _identityClient;
    private readonly ITenantClient _tenantClient;
    private readonly IOpenIddictClaimsPrincipalHandler _claimsPrincipalHandler;
    private readonly ITokenMetadataReadService _tokenMetadataReadService;
    private readonly RoleClaimsPrincipalService _roleClaimsPrincipalService;
    private readonly IAuditLogWriteRequestPublisher _auditLogPublisher;
    private readonly ILogger<TokenController> _logger;

    public TokenController(
        IIdentityClient identityClient,
        ITenantClient tenantClient,
        IOpenIddictClaimsPrincipalHandler claimsPrincipalHandler,
        RoleClaimsPrincipalService roleClaimsPrincipalService,
        ITokenMetadataReadService tokenMetadataReadService,
        IAuditLogWriteRequestPublisher auditLogPublisher,
        ILogger<TokenController> logger)
    {
        _identityClient = identityClient;
        _tenantClient = tenantClient;
        _claimsPrincipalHandler = claimsPrincipalHandler;
        _roleClaimsPrincipalService = roleClaimsPrincipalService;
        _tokenMetadataReadService = tokenMetadataReadService;
        _auditLogPublisher = auditLogPublisher;
        _logger = logger;
    }

    [HttpPost("~/connect/token"), IgnoreAntiforgeryToken]
    [AllowAnonymous]
    [EnableRateLimiting("token")]
    [Consumes("application/x-www-form-urlencoded")]
    [Produces("application/json")]
    public async Task<IActionResult> HandleAsync()
    {
        var request = HttpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

        if (request.IsPasswordGrantType())
        {
            return await HandlePasswordGrantAsync(request);
        }

        if (request.IsClientCredentialsGrantType())
        {
            return await HandleClientCredentialsGrantAsync(request);
        }

        if (request.IsAuthorizationCodeGrantType() || request.IsRefreshTokenGrantType())
        {
            return await HandleAuthorizationCodeAndRefreshGrantAsync(request);
        }

        await TryPublishAuditFailureAsync("unsupported_grant_type", request, tenantId: null, userId: null);
        return BuildOpenIddictForbid(
            OpenIddictConstants.Errors.UnsupportedGrantType,
            "The specified grant type is not supported.");
    }

    private async Task<(TenantResolutionStatus Status, Guid? TenantId)> ResolveTenantIdAsync(OpenIddictRequest request)
    {
        var tenantIdClaimAlias = request.GetParameter(SharedClaimTypes.TenantId)?.ToString();
        var tenantIdRequestValue = request.GetParameter("tenant_id")?.ToString();
        if (!string.IsNullOrWhiteSpace(tenantIdClaimAlias) &&
            !string.IsNullOrWhiteSpace(tenantIdRequestValue) &&
            !string.Equals(tenantIdClaimAlias, tenantIdRequestValue, StringComparison.OrdinalIgnoreCase))
        {
            return (TenantResolutionStatus.Invalid, null);
        }

        var tenantIdValue = string.IsNullOrWhiteSpace(tenantIdRequestValue)
            ? tenantIdClaimAlias
            : tenantIdRequestValue;
        if (!string.IsNullOrWhiteSpace(tenantIdValue))
        {
            if (!Guid.TryParse(tenantIdValue, out var tenantId) || tenantId == Guid.Empty)
            {
                return (TenantResolutionStatus.Invalid, null);
            }

            var tenantById = await _tenantClient.FindByIdAsync(tenantId, HttpContext.RequestAborted);
            if (tenantById == null || !tenantById.IsActive)
            {
                return (TenantResolutionStatus.Invalid, null);
            }

            return (TenantResolutionStatus.Resolved, tenantById.Id);
        }

        var tenantName = request.GetParameter("tenant")?.ToString();
        if (string.IsNullOrWhiteSpace(tenantName))
        {
            return (TenantResolutionStatus.NoneProvided, null);
        }

        var normalizedName = tenantName.Trim().ToUpperInvariant();
        var tenantByName = await _tenantClient.FindByNormalizedNameAsync(normalizedName, HttpContext.RequestAborted);
        if (tenantByName == null || !tenantByName.IsActive)
        {
            return (TenantResolutionStatus.Invalid, null);
        }

        return (TenantResolutionStatus.Resolved, tenantByName.Id);
    }

    private ForbidResult BuildOpenIddictForbid(string error, string errorDescription)
    {
        return Forbid(
            authenticationSchemes: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
            properties: new AuthenticationProperties(new Dictionary<string, string?>
            {
                [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
                [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = errorDescription
            }));
    }

    private async Task<IReadOnlyList<string>> GetResourcesAsync(ImmutableArray<string> scopes)
    {
        return await _tokenMetadataReadService.GetResourcesForScopesAsync(scopes, HttpContext.RequestAborted);
    }

    private async Task<ClaimsPrincipal?> TryRefreshUserClaimsAsync(ClaimsPrincipal currentPrincipal, Guid userId, Guid? tenantId, CancellationToken cancellationToken)
    {
        var userInfo = await _identityClient.GetUserInfoByIdAsync(
            userId,
            tenantId,
            strictAuthorization: true,
            bypassCache: false,
            cancellationToken: cancellationToken);
        if (userInfo == null)
        {
            return null;
        }

        var identity = new ClaimsIdentity(
            authenticationType: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
            nameType: OpenIddictConstants.Claims.Name,
            roleType: OpenIddictConstants.Claims.Role);

        identity.AddClaim(OpenIddictConstants.Claims.Subject, userInfo.Id.ToString());
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
            identity.AddClaim(SharedClaimTypes.TenantId, userInfo.TenantId.Value.ToString());
        }

        var sessionId = currentPrincipal.FindFirst(SharedClaimTypes.SessionId)?.Value;
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            identity.AddClaim(SharedClaimTypes.SessionId, sessionId);
        }

        var refreshedPrincipal = new ClaimsPrincipal(identity);
        refreshedPrincipal.SetScopes(currentPrincipal.GetScopes());

        if (!_roleClaimsPrincipalService.TryApply(refreshedPrincipal, userInfo, out var roleClaimsFailure))
        {
            _logger.LogWarning("Token claim rebuild rejected. Reason={Reason}", roleClaimsFailure);
            return null;
        }

        try
        {
            refreshedPrincipal.SetResources(currentPrincipal.GetResources().IsDefaultOrEmpty
                ? await GetResourcesAsync(refreshedPrincipal.GetScopes())
                : currentPrincipal.GetResources());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Token metadata resolution failed during refresh token claim rebuild.");
            return null;
        }

        await _claimsPrincipalHandler.HandleAsync(refreshedPrincipal);
        return refreshedPrincipal;
    }

    private async Task<IActionResult?> TrySetResourcesAsync(ClaimsPrincipal principal)
    {
        try
        {
            principal.SetResources(await GetResourcesAsync(principal.GetScopes()));
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Token metadata resolution failed for token issuance.");
            return BuildOpenIddictForbid(OpenIddictConstants.Errors.InvalidRequest, "Token metadata resolution failed.");
        }
    }

    private async Task TryPublishAuditFailureAsync(
        string reason,
        OpenIddictRequest request,
        Guid? tenantId,
        Guid? userId)
    {
        var cancellationToken = HttpContext.RequestAborted;

        try
        {
            await _auditLogPublisher.PublishAsync(
                new AuditLogWriteRequest
                {
                    EventId = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    OccurredAtUtc = DateTime.UtcNow,
                    Category = "auth.token.failure",
                    UserId = userId?.ToString("D"),
                    CorrelationId = HttpContext.TraceIdentifier,
                    TraceId = System.Diagnostics.Activity.Current?.TraceId.ToString(),
                    HttpMethod = HttpContext.Request.Method,
                    Path = HttpContext.Request.Path.Value,
                    StatusCode = StatusCodes.Status401Unauthorized,
                    Comments = [reason],
                    Exceptions =
                    [
                        new AuditLogWriteRequestException
                        {
                            Type = "auth.token.failure",
                            Message = reason
                        }
                    ],
                    RedactionApplied = true,
                    RedactionVersion = 1,
                    Actions =
                    [
                        new AuditLogWriteRequestAction
                        {
                            Name = "auth.token.request.failure",
                            Result = "failure",
                            Parameters = new Dictionary<string, string?>(StringComparer.Ordinal)
                            {
                                ["grantType"] = request.GrantType,
                                ["clientId"] = request.ClientId,
                                ["reason"] = reason
                            }
                        }
                    ]
                },
                cancellationToken);
        }
        catch (Exception exception)
        {
            // Audit transport failure must not turn a deterministic authentication denial into a 500.
            // The request remains denied; the transport failure is retained in structured service logs.
            _logger.LogError(exception, "Unable to publish token failure audit event for reason {Reason}.", reason);
        }
    }

    private static string ResolvePasswordGrantFailureReason(
        LoginResultDto loginResult,
        string? twoFactorCode,
        string? twoFactorRecoveryCode)
    {
        if (loginResult.BlockedByRisk)
        {
            return "password_login_risk_blocked";
        }

        if (loginResult.IsLockedOut)
        {
            return "password_login_locked_out";
        }

        if (loginResult.ShouldChangePasswordOnNextLogin)
        {
            return "password_change_required";
        }

        if (loginResult.RequiresTwoFactor)
        {
            var hasCode = !string.IsNullOrWhiteSpace(twoFactorCode) || !string.IsNullOrWhiteSpace(twoFactorRecoveryCode);
            return hasCode ? "password_login_two_factor_invalid" : "password_login_two_factor_required";
        }

        if (loginResult.IsNotAllowed)
        {
            return "password_login_not_allowed";
        }

        return "password_login_invalid_credentials";
    }

}
