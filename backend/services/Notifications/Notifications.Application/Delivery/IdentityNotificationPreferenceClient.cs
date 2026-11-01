using Notifications.Application.Contracts.Delivery;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Modularity.DependencyInjection;

namespace Notifications.Application.Delivery;

public sealed class IdentityNotificationPreferenceClient : IIdentityNotificationPreferenceClient, ITransientDependency
{
    public const string HttpClientName = "Notifications.IdentityPreferences";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly InternalAccessTokenProvider _tokenProvider;
    private readonly IOptions<IdentityNotificationPreferenceClientOptions> _options;
    private readonly ILogger<IdentityNotificationPreferenceClient> _logger;

    public IdentityNotificationPreferenceClient(
        IHttpClientFactory httpClientFactory,
        InternalAccessTokenProvider tokenProvider,
        IOptions<IdentityNotificationPreferenceClientOptions> options,
        ILogger<IdentityNotificationPreferenceClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _tokenProvider = tokenProvider;
        _options = options;
        _logger = logger;
    }

    public async Task<bool> IsNotificationEnabledAsync(
        Guid? tenantId,
        Guid userId,
        string preferenceKey,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty || string.IsNullOrWhiteSpace(preferenceKey))
        {
            return false;
        }

        var options = _options.Value;
        if (!options.Enabled || !Uri.TryCreate(options.BaseAddress, UriKind.Absolute, out _))
        {
            return !options.FailClosed;
        }

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"api/identity/internal/profile-preferences/users/{userId:D}/alerts/{Uri.EscapeDataString(preferenceKey)}");

            if (tenantId.HasValue)
            {
                request.Headers.TryAddWithoutValidation("X-Tenant-Id", tenantId.Value.ToString("D"));
            }

            var token = await _tokenProvider.GetAccessTokenForClientAsync(
                options.ClientProfile, options.Scope, cancellationToken);
            if (string.IsNullOrWhiteSpace(token))
            {
                // Never call Identity anonymously; the provider already logged why the token is unavailable.
                _logger.LogWarning(
                    "Identity preference lookup has no M2M access token for client profile {ClientProfile}; UserId={UserId}, TenantId={TenantId}, PreferenceKey={PreferenceKey}.",
                    options.ClientProfile,
                    userId,
                    tenantId,
                    preferenceKey);
                return !options.FailClosed;
            }

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Identity preference lookup returned {StatusCode} for UserId={UserId}, TenantId={TenantId}, PreferenceKey={PreferenceKey}.",
                    response.StatusCode,
                    userId,
                    tenantId,
                    preferenceKey);
                return !options.FailClosed;
            }

            var payload = await response.Content.ReadFromJsonAsync<PreferenceLookupResponse>(cancellationToken: cancellationToken);
            return payload?.Enabled ?? !options.FailClosed;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Identity preference lookup failed for UserId={UserId}, TenantId={TenantId}, PreferenceKey={PreferenceKey}.",
                userId,
                tenantId,
                preferenceKey);
            return !options.FailClosed;
        }
    }

    private sealed class PreferenceLookupResponse
    {
        public bool Enabled { get; set; }
    }
}
