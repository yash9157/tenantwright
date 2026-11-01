using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Modularity.DependencyInjection;
using Shared.Permissions.Features;

namespace Tenant.Application.Tenants;

public sealed class TenantImpactEnrichmentClient : ITransientDependency
{
    public const string IdentityHttpClientName = "Tenant.Impact.Identity";
    public const string NotificationsHttpClientName = "Tenant.Impact.Notifications";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IInternalAccessTokenProvider _tokenProvider;
    private readonly IOptions<TenantImpactEnrichmentOptions> _options;
    private readonly ILogger<TenantImpactEnrichmentClient> _logger;

    public TenantImpactEnrichmentClient(
        IHttpClientFactory httpClientFactory,
        IInternalAccessTokenProvider tokenProvider,
        IOptions<TenantImpactEnrichmentOptions> options,
        ILogger<TenantImpactEnrichmentClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _tokenProvider = tokenProvider;
        _options = options;
        _logger = logger;
    }

    public async Task<TenantImpactEnrichmentResult> GetAsync(Guid tenantId)
    {
        var identityTask = GetCountAsync(
            IdentityHttpClientName,
            "identity-internal",
            "api/identity/internal/sessions/active-count",
            tenantId,
            static response => response.ActiveSessionCount,
            "Identity");
        var notificationsTask = GetCountAsync(
            NotificationsHttpClientName,
            "notifications-internal",
            "api/notifications/internal/tenant-deliveries/pending-count",
            tenantId,
            static response => response.PendingDeliveryCount,
            "Notifications");

        await Task.WhenAll(identityTask, notificationsTask);
        return new TenantImpactEnrichmentResult(identityTask.Result, notificationsTask.Result);
    }

    private async Task<TenantImpactDependencyCount> GetCountAsync(
        string clientName,
        string scope,
        string endpoint,
        Guid tenantId,
        Func<TenantImpactCountResponse, int> countSelector,
        string dependencyName)
    {
        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(_options.Value.TimeoutMilliseconds, 250, 5000));
        using var timeoutCancellation = new CancellationTokenSource(timeout);
        try
        {
            var token = await _tokenProvider.GetAccessTokenForClientAsync(
                _options.Value.ClientProfile,
                scope,
                timeoutCancellation.Token);
            if (string.IsNullOrWhiteSpace(token))
            {
                _logger.LogWarning("{DependencyName} impact enrichment has no M2M access token.", dependencyName);
                return TenantImpactDependencyCount.Unavailable;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{endpoint}?tenantId={tenantId:D}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await _httpClientFactory.CreateClient(clientName).SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutCancellation.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "{DependencyName} impact enrichment returned HTTP {StatusCode} for TenantId={TenantId}.",
                    dependencyName,
                    (int)response.StatusCode,
                    tenantId);
                return TenantImpactDependencyCount.Unavailable;
            }

            var payload = await response.Content.ReadFromJsonAsync<TenantImpactCountResponse>(
                cancellationToken: timeoutCancellation.Token);
            return payload == null
                ? TenantImpactDependencyCount.Unavailable
                : new TenantImpactDependencyCount(countSelector(payload), IsAvailable: true);
        }
        catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested)
        {
            _logger.LogWarning("{DependencyName} impact enrichment timed out for TenantId={TenantId}.", dependencyName, tenantId);
            return TenantImpactDependencyCount.Unavailable;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "{DependencyName} impact enrichment failed for TenantId={TenantId}.", dependencyName, tenantId);
            return TenantImpactDependencyCount.Unavailable;
        }
    }

    private sealed class TenantImpactCountResponse
    {
        public int ActiveSessionCount { get; set; }
        public int PendingDeliveryCount { get; set; }
    }
}

public sealed record TenantImpactEnrichmentResult(
    TenantImpactDependencyCount Identity,
    TenantImpactDependencyCount Notifications);

public sealed record TenantImpactDependencyCount(int? Count, bool IsAvailable)
{
    public static TenantImpactDependencyCount Unavailable { get; } = new(null, IsAvailable: false);
}
