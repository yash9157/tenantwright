using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Caching.Sessions;
using Shared.Modularity.DependencyInjection;

namespace Shared.AspNetCore.Host.Middleware;

[ExposeServices(typeof(SessionValidationMiddleware))]
public sealed class SessionValidationMiddleware : IMiddleware, ITransientDependency
{
    private const string SessionIdClaimName = "session_id";
    private const string TenantIdClaimName = "tenantid";
    private const string SubjectClaimName = "sub";
    private const string OpenIddictValidationScheme = "OpenIddict.Validation.AspNetCore";

    private readonly ISessionMarkerStore _sessionMarkerStore;
    private readonly IOptions<SessionValidationOptions> _options;
    private readonly ILogger<SessionValidationMiddleware> _logger;

    public SessionValidationMiddleware(
        ISessionMarkerStore sessionMarkerStore,
        IOptions<SessionValidationOptions> options,
        ILogger<SessionValidationMiddleware> logger)
    {
        _sessionMarkerStore = sessionMarkerStore;
        _options = options;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (!_options.Value.Enabled)
        {
            await next(context);
            return;
        }

        if (ShouldSkip(context))
        {
            await next(context);
            return;
        }

        var principal = context.User;
        if (principal.Identity?.IsAuthenticated != true)
        {
            var authResult = await context.AuthenticateAsync(OpenIddictValidationScheme);
            if (authResult.Succeeded && authResult.Principal?.Identity?.IsAuthenticated == true)
            {
                principal = authResult.Principal;
                context.User = principal;
            }
        }

        if (principal.Identity?.IsAuthenticated != true)
        {
            if (context.Request.Headers.TryGetValue("Authorization", out var authorizationHeader)
                && authorizationHeader.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Skipping session validation because bearer authentication did not produce an authenticated principal.");
            }
            await next(context);
            return;
        }

        var subject = principal.FindFirstValue(SubjectClaimName) ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(subject, out var userId))
        {
            // Client credentials tokens do not carry user sessions.
            _logger.LogDebug("Skipping session validation because subject claim is not a GUID. Subject={Subject}", subject);
            await next(context);
            return;
        }

        var sessionId = principal.FindFirstValue(SessionIdClaimName);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            var clientId = principal.FindFirstValue("client_id");
            if (!string.IsNullOrWhiteSpace(clientId))
            {
                _logger.LogDebug("Skipping session validation because client_id is present and session_id is missing (client credentials token). ClientId={ClientId}", clientId);
                await next(context);
                return;
            }

            _logger.LogWarning("Rejecting authenticated user request due to missing session_id claim. UserId={UserId}", userId);
            await RejectUnauthorizedAsync(context, "invalid_session", "Missing session_id claim.");
            return;
        }

        Guid? tenantId = null;
        var tenantIdClaim = principal.FindFirstValue(TenantIdClaimName);
        if (Guid.TryParse(tenantIdClaim, out var parsedTenantId))
        {
            tenantId = parsedTenantId;
        }

        string? markerValue;
        try
        {
            markerValue = await _sessionMarkerStore.IsActiveAsync(
                tenantId,
                userId,
                sessionId,
                context.RequestAborted);
        }
        catch (Exception ex)
        {
            if (!_options.Value.Strict)
            {
                _logger.LogWarning(
                    ex,
                    "Session validation failed due to Valkey/cache error but Strict=false. Allowing request. UserId={UserId}",
                    userId);
                await next(context);
                return;
            }

            _logger.LogError(
                ex,
                "Session validation failed due to Valkey/cache error. Rejecting request because Strict=true. UserId={UserId}",
                userId);
            await RejectUnauthorizedAsync(context, "invalid_session", "Session validation unavailable.");
            return;
        }

        if (markerValue == null)
        {
            if (!_options.Value.Strict)
            {
                _logger.LogWarning(
                    "Session marker is missing but Strict=false. Allowing request. UserId={UserId}",
                    userId);
                await next(context);
                return;
            }

            _logger.LogInformation(
                "Rejecting request with revoked/invalid session. UserId={UserId}",
                userId);
            await RejectUnauthorizedAsync(context, "invalid_session", "Session revoked or expired.");
            return;
        }

        var now = DateTime.UtcNow;
        var idleTimeout = TimeSpan.FromMinutes(_options.Value.IdleTimeoutMinutes);

        if (markerValue == "1")
        {
            // Migrate a legacy presence-only marker to the abs:idle payload format.
            var (migratedPayload, migratedTtl) = SessionMarkerPayload.CreateInitial(
                _options.Value.MarkerTtlHours,
                _options.Value.IdleTimeoutMinutes,
                now);

            try
            {
                await _sessionMarkerStore.SetActiveAsync(
                    tenantId,
                    userId,
                    sessionId,
                    migratedPayload,
                    migratedTtl,
                    context.RequestAborted);
            }
            catch (Exception ex)
            {
                if (!_options.Value.Strict)
                {
                    _logger.LogWarning(
                        ex,
                        "Failed to migrate legacy session marker but Strict=false. Allowing request. UserId={UserId}",
                        userId);
                }
                else
                {
                    _logger.LogError(
                        ex,
                        "Failed to migrate legacy session marker. Rejecting request because Strict=true. UserId={UserId}",
                        userId);
                    await RejectUnauthorizedAsync(context, "invalid_session", "Session validation unavailable.");
                    return;
                }
            }
        }
        else
        {
            if (!SessionMarkerPayload.TryParse(markerValue, out var absExpiry, out var idleExpiry))
            {
                _logger.LogWarning("Rejecting request due to malformed session marker format. UserId={UserId}", userId);
                await RejectUnauthorizedAsync(context, "invalid_session", "Malformed session marker.");
                return;
            }

            if (now >= absExpiry)
            {
                try
                {
                    await _sessionMarkerStore.RemoveAsync(tenantId, userId, sessionId, context.RequestAborted);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to remove absolute-expired session marker for UserId={UserId}", userId);
                }

                _logger.LogInformation("Rejecting request due to absolute session expiration. UserId={UserId}", userId);
                await RejectUnauthorizedAsync(context, "invalid_session", "Session absolute timeout reached.");
                return;
            }

            if (now >= idleExpiry)
            {
                _logger.LogInformation("Rejecting request due to idle session expiration. UserId={UserId}", userId);
                await RejectUnauthorizedAsync(context, "invalid_session", "Session idle timeout reached.");
                return;
            }

            var remainingIdle = idleExpiry - now;
            var throttleThreshold = TimeSpan.FromMinutes(_options.Value.IdleTimeoutMinutes / 2.0);

            if (remainingIdle < throttleThreshold)
            {
                var potentialIdleExpiry = now.Add(idleTimeout);
                var newIdleExpiry = potentialIdleExpiry < absExpiry ? potentialIdleExpiry : absExpiry;
                var keyTtl = newIdleExpiry - now;
                var newPayload = SessionMarkerPayload.Format(absExpiry, newIdleExpiry);

                try
                {
                    await _sessionMarkerStore.SetActiveAsync(
                        tenantId,
                        userId,
                        sessionId,
                        newPayload,
                        keyTtl,
                        context.RequestAborted);
                }
                catch (Exception ex)
                {
                    if (!_options.Value.Strict)
                    {
                        _logger.LogWarning(
                            ex,
                            "Failed to refresh throttled session marker but Strict=false. Allowing request. UserId={UserId}",
                            userId);
                    }
                    else
                    {
                        _logger.LogError(
                            ex,
                            "Failed to refresh throttled session marker. Rejecting request because Strict=true. UserId={UserId}",
                            userId);
                        await RejectUnauthorizedAsync(context, "invalid_session", "Session validation unavailable.");
                        return;
                    }
                }
            }
        }

        await next(context);
    }

    private static bool ShouldSkip(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase) ||
            context.Request.Path.StartsWithSegments("/alive", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var endpoint = context.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<IAllowAnonymous>() != null)
        {
            return true;
        }

        return false;
    }

    private static Task RejectUnauthorizedAsync(HttpContext context, string error, string description)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return context.Response.WriteAsJsonAsync(new
        {
            error,
            error_description = description
        });
    }
}
