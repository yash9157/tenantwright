using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Notifications.Application.Contracts.Channels;
using Notifications.Application.Contracts.ProviderConfig;
using Shared.Modularity.DependencyInjection;

namespace Notifications.Application.Delivery.Providers;

/// <summary>
/// Sends push notifications through Firebase Cloud Messaging's HTTP v1 API. Exchanges the config's
/// service-account credential for a short-lived OAuth2 access token per send. Neither the private
/// key nor the access token is ever logged.
/// </summary>
[DisableConventionalRegistration]
public sealed class FcmPushProviderAdapter : IPushProviderAdapter, ITransientDependency
{
    public const string HttpClientName = "Notifications.Fcm.Push";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly FcmAccessTokenProvider _accessTokenProvider;
    private readonly ILogger<FcmPushProviderAdapter> _logger;

    public FcmPushProviderAdapter(
        IHttpClientFactory httpClientFactory,
        FcmAccessTokenProvider accessTokenProvider,
        ILogger<FcmPushProviderAdapter> logger)
    {
        _httpClientFactory = httpClientFactory;
        _accessTokenProvider = accessTokenProvider;
        _logger = logger;
    }

    public string ProviderName => "Fcm";

    public async Task<PushProviderSendResult> SendAsync(
        string deviceToken,
        string title,
        string body,
        ResolvedNotificationProviderConfig config,
        CancellationToken cancellationToken = default)
    {
        var settings = JsonSerializer.Deserialize<FcmSettings>(config.SettingsJson, JsonOptions) ?? new FcmSettings();
        var secrets = DeserializeSecrets(config.Secret);

        if (string.IsNullOrWhiteSpace(settings.ProjectId) ||
            string.IsNullOrWhiteSpace(secrets.ClientEmail) ||
            string.IsNullOrWhiteSpace(secrets.PrivateKey))
        {
            throw new InvalidOperationException(
                $"FCM configuration '{config.ConfigId}' is missing ProjectId, ClientEmail, or PrivateKey.");
        }

        string accessToken;
        try
        {
            accessToken = await _accessTokenProvider.GetAccessTokenAsync(secrets.ClientEmail, secrets.PrivateKey, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or CryptographicException or FormatException)
        {
            _logger.LogWarning(ex, "FCM access token exchange failed for configuration {ConfigId}.", config.ConfigId);
            return PushProviderSendResult.Failed("fcm_auth_failed");
        }

        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://fcm.googleapis.com/v1/projects/{Uri.EscapeDataString(settings.ProjectId)}/messages:send");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var payload = new
        {
            message = new
            {
                token = deviceToken,
                notification = new { title, body }
            }
        };
        request.Content = JsonContent.Create(payload, options: JsonOptions);

        using var response = await client.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            var messageName = TryReadMessageName(responseBody);
            return PushProviderSendResult.Success(messageName);
        }

        var errorCode = TryReadErrorStatus(responseBody) ?? ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture);
        // FCM's error body can include the device token but never the access token or private key;
        // log only the vendor error status and HTTP status.
        _logger.LogWarning(
            "FCM push send failed. HttpStatus={HttpStatus}, FcmStatus={FcmStatus}.",
            (int)response.StatusCode,
            errorCode);
        return PushProviderSendResult.Failed($"fcm_{errorCode}");
    }

    private static FcmSecrets DeserializeSecrets(string? secretJson)
    {
        if (string.IsNullOrWhiteSpace(secretJson))
        {
            return new FcmSecrets();
        }

        try
        {
            return JsonSerializer.Deserialize<FcmSecrets>(secretJson, JsonOptions) ?? new FcmSecrets();
        }
        catch (JsonException)
        {
            return new FcmSecrets();
        }
    }

    private static string? TryReadMessageName(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("name", out var name) ? name.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TryReadErrorStatus(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("error", out var error) &&
                error.TryGetProperty("status", out var status))
            {
                return status.GetString();
            }
        }
        catch (JsonException)
        {
            // Non-JSON body; nothing to extract.
        }

        return null;
    }
}
