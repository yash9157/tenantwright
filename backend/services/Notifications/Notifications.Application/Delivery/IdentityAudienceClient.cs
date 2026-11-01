using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Notifications.Application.Contracts.Delivery;
using Notifications.Domain.Shared.Constants;
using Shared;
using Shared.Modularity.DependencyInjection;

namespace Notifications.Application.Delivery;

/// <summary>
/// Calls Identity's internal user-audience endpoints (M2M, cached client-credentials token) to
/// expand role / all-tenant audiences into user ids. Fail-closed: any transport/HTTP failure throws
/// so the schedule is retried rather than sending to a partial audience.
/// </summary>
public sealed class IdentityAudienceClient : IIdentityAudienceClient, ITransientDependency
{
    public const string HttpClientName = "Notifications.IdentityAudience";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly InternalAccessTokenProvider _tokenProvider;
    private readonly IOptions<IdentityAudienceClientOptions> _options;
    private readonly ILogger<IdentityAudienceClient> _logger;

    public IdentityAudienceClient(
        IHttpClientFactory httpClientFactory,
        InternalAccessTokenProvider tokenProvider,
        IOptions<IdentityAudienceClientOptions> options,
        ILogger<IdentityAudienceClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _tokenProvider = tokenProvider;
        _options = options;
        _logger = logger;
    }

    public Task<IReadOnlyList<Guid>> GetUsersByRoleAsync(Guid tenantId, string roleName, int maxUsers, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(roleName))
        {
            throw new SharedValidationException(
                NotificationErrorCodes.AudienceRoleRequired,
                "Role name is required for role audience resolution.");
        }

        return CollectAsync(tenantId, $"roles/{Uri.EscapeDataString(roleName.Trim())}/users", maxUsers, cancellationToken);
    }

    public Task<IReadOnlyList<Guid>> GetTenantUserIdsAsync(Guid tenantId, int maxUsers, CancellationToken cancellationToken = default)
    {
        return CollectAsync(tenantId, "tenant-users", maxUsers, cancellationToken);
    }

    private async Task<IReadOnlyList<Guid>> CollectAsync(Guid tenantId, string relativePath, int maxUsers, CancellationToken cancellationToken)
    {
        var options = _options.Value;
        if (!options.Enabled || !Uri.TryCreate(options.BaseAddress, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException("Identity audience client is not configured (Notifications:IdentityAudience:BaseAddress).");
        }

        var pageSize = Math.Clamp(options.PageSize, 1, 500);
        var results = new List<Guid>();
        var skip = 0;

        var token = await _tokenProvider.GetAccessTokenForClientAsync(
            options.ClientProfile, options.Scope, cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("Identity audience resolution could not acquire its dedicated M2M access token.");
        }

        while (results.Count <= maxUsers)
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"api/identity/internal/user-audience/{relativePath}?skipCount={skip}&maxResultCount={pageSize}");
            request.Headers.TryAddWithoutValidation("X-Tenant-Id", tenantId.ToString("D"));
            if (!string.IsNullOrWhiteSpace(token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Identity audience lookup returned {StatusCode} for TenantId={TenantId}, Path={Path}.",
                    response.StatusCode, tenantId, relativePath);
                throw new InvalidOperationException($"Identity audience lookup failed with status {(int)response.StatusCode}.");
            }

            var page = await response.Content.ReadFromJsonAsync<AudiencePageResponse>(cancellationToken: cancellationToken);
            var ids = page?.UserIds;
            if (ids == null || ids.Count == 0)
            {
                break;
            }

            results.AddRange(ids);
            if (ids.Count < pageSize)
            {
                break;
            }

            skip += pageSize;
        }

        return results;
    }

    private sealed class AudiencePageResponse
    {
        public List<Guid> UserIds { get; set; } = [];
        public int TotalCount { get; set; }
    }
}
