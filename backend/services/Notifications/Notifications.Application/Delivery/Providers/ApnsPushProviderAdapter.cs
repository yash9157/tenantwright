using Notifications.Domain.Shared.Enums;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Notifications.Application.Contracts.Channels;
using Notifications.Application.Contracts.ProviderConfig;
using Shared.Modularity.DependencyInjection;

namespace Notifications.Application.Delivery.Providers;

/// <summary>APNs HTTP/2 token-auth adapter. Sandbox selection is configuration-owned and explicit.</summary>
[DisableConventionalRegistration]
public sealed class ApnsPushProviderAdapter : IPushProviderAdapter, ITransientDependency
{
    public const string HttpClientName = "Notifications.Apns.Push";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ApnsPushProviderAdapter> _logger;

    public ApnsPushProviderAdapter(IHttpClientFactory httpClientFactory, ILogger<ApnsPushProviderAdapter> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public string ProviderName => "Apns";

    public async Task<NotificationProviderSendResult> SendAsync(string deviceToken, string title, string body, ResolvedNotificationProviderConfig config, CancellationToken cancellationToken = default)
    {
        using var sendActivity = NotificationProviderTrace.Start(NotificationChannel.Push);
        var settings = JsonSerializer.Deserialize<ApnsSettings>(config.SettingsJson, JsonOptions) ?? new();
        var secrets = JsonSerializer.Deserialize<ApnsSecrets>(config.Secret ?? "{}", JsonOptions) ?? new();
        if (string.IsNullOrWhiteSpace(settings.TeamId) || string.IsNullOrWhiteSpace(settings.KeyId) || string.IsNullOrWhiteSpace(settings.BundleId) || string.IsNullOrWhiteSpace(secrets.PrivateKey))
            throw new global::Notifications.Application.Contracts.Delivery.NotificationChannelUnavailableException("apns_configuration_unavailable", "APNs configuration is incomplete.");

        var host = settings.UseSandbox ? "https://api.sandbox.push.apple.com" : "https://api.push.apple.com";
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{host}/3/device/{Uri.EscapeDataString(deviceToken)}")
        {
            Content = JsonContent.Create(new { aps = new { alert = new { title, body }, sound = "default" } })
        };
        request.Version = HttpVersion.Version20;
        request.Headers.Authorization = new AuthenticationHeaderValue("bearer", CreateProviderToken(settings, secrets.PrivateKey));
        request.Headers.TryAddWithoutValidation("apns-topic", settings.BundleId);
        using var response = await _httpClientFactory.CreateClient(HttpClientName)
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.IsSuccessStatusCode)
            return NotificationProviderSendResult.Success(response.Headers.TryGetValues("apns-id", out var values) ? values.FirstOrDefault() : null);

        var error = response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Gone or HttpStatusCode.NotFound
            ? "apns_bad_device_token" : $"apns_{(int)response.StatusCode}";
        _logger.LogWarning("APNs delivery failed for configuration {ConfigId} with status {StatusCode}.", config.ConfigId, (int)response.StatusCode);
        return NotificationHttpResponseOutcome.Rejection(response, error, TimeSpan.FromMinutes(15), documentedServerRejection: true);
    }

    private static string CreateProviderToken(ApnsSettings settings, string privateKey)
    {
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "ES256", kid = settings.KeyId }));
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { iss = settings.TeamId, iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }));
        var unsigned = Encoding.ASCII.GetBytes($"{header}.{payload}");
        using var key = ECDsa.Create();
        key.ImportFromPem(privateKey);
        return $"{header}.{payload}.{Base64Url(key.SignData(unsigned, HashAlgorithmName.SHA256))}";
    }

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
