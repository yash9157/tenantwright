using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Auth.Caching;
using Auth.Clients.Caching;
using Identity.Application.Contracts.Account;
using Identity.Domain.Shared.Account;
using Identity.Application.Contracts.Users;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenIddict.Server;
using Shared.Caching.Abstractions;
using Shared.Caching.Sessions;
using Shared.DistributedLocking.Abstractions;
using Shared.DistributedLocking.Options;
using Shared.DistributedLocking.Policies;

namespace Auth.Clients;

public partial class IdentityClient
{
    public async Task RevokeSessionAsync(Guid userId, string sessionId, Guid? tenantId = null, CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty || string.IsNullOrWhiteSpace(sessionId) || tenantId == Guid.Empty)
        {
            throw new InvalidOperationException("User and session context are required to revoke an Identity session.");
        }

        // Remove the shared marker before the remote write. This makes all API
        // validation fail closed even when Identity is temporarily unavailable;
        // the idempotent Identity call persists and publishes the revocation.
        await _sessionMarkerStore.RemoveAsync(tenantId, userId, sessionId, cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/identity/internal/sessions/revoke")
        {
            Content = JsonContent.Create(new { UserId = userId, SessionId = sessionId, TenantId = tenantId })
        };
        request.Headers.TryAddWithoutValidation(
            "Idempotency-Key",
            BuildSessionRevocationIdempotencyKey(tenantId, userId, sessionId));
        AddTenantHeader(request, tenantId);
        await AddInternalAuthorizationAsync(request, cancellationToken);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static string BuildSessionRevocationIdempotencyKey(
        Guid? tenantId,
        Guid userId,
        string sessionId)
    {
        var scope = tenantId?.ToString("D") ?? "host";
        var payload = Encoding.UTF8.GetBytes($"{scope}:{userId:D}:{sessionId}");
        return $"logout-{Convert.ToHexString(SHA256.HashData(payload))}";
    }

    public async Task<bool> ValidateSessionAsync(Guid userId, string sessionId, Guid? tenantId = null, CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty || string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        try
        {
            var markerHit = await _sessionMarkerStore.IsActiveAsync(tenantId, userId, sessionId, cancellationToken);
            if (markerHit != null)
            {
                return true;
            }

            if (!_sessionValidationOptions.EnableDbFallbackOnValkeyMiss)
            {
                _logger.LogWarning(
                    "Session marker cache miss for user token. UserId={UserId}",
                    userId);
                return false;
            }
        }
        catch (Exception ex)
        {
            if (!_sessionValidationOptions.EnableDbFallbackOnValkeyMiss)
            {
                _logger.LogWarning(
                    ex,
                    "Session marker validation failed and DB fallback is disabled. UserId={UserId}",
                    userId);
                return false;
            }

            _logger.LogWarning(
                ex,
                "Session marker validation failed. Falling back to Identity DB validation. UserId={UserId}",
                userId);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/identity/internal/sessions/validate")
            {
                Content = JsonContent.Create(new { UserId = userId, SessionId = sessionId, TenantId = tenantId })
            };
            AddTenantHeader(request, tenantId);
            await AddInternalAuthorizationAsync(request, cancellationToken);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            return await response.Content.ReadFromJsonAsync<bool>(cancellationToken: cancellationToken) == true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error communicating with Identity service for validate-session fallback.");
            return false;
        }
    }

    public async Task<List<IdentitySessionDto>> GetSessionsAsync(Guid userId, Guid? tenantId = null, CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty)
        {
            return new List<IdentitySessionDto>();
        }

        try
        {
            if (!tenantId.HasValue || tenantId.Value == Guid.Empty)
            {
                return new List<IdentitySessionDto>();
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, $"api/identity/internal/sessions/list?userId={userId}&tenantId={tenantId.Value:D}");
            AddTenantHeader(request, tenantId);
            await AddInternalAuthorizationAsync(request, cancellationToken);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new List<IdentitySessionDto>();
            }

            return await response.Content.ReadFromJsonAsync<List<IdentitySessionDto>>(cancellationToken: cancellationToken)
                   ?? new List<IdentitySessionDto>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error communicating with Identity service for list-sessions.");
            return new List<IdentitySessionDto>();
        }
    }

}
