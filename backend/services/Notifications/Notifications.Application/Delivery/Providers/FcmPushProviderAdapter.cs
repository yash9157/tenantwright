using Notifications.Domain.Shared.Enums;
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

    public async Task<NotificationProviderSendResult> SendAsync(
        string deviceToken,
        string title,
        string body,
        ResolvedNotificationProviderConfig config,
        CancellationToken cancellationToken = default)
    {
        using var sendActivity = NotificationProviderTrace.Start(NotificationChannel.Push);
        var settings = JsonSerializer.Deserialize<FcmSettings>(config.SettingsJson, JsonOptions) ?? new FcmSettings();
        var secrets = DeserializeSecrets(config.Secret);

        if (string.IsNullOrWhiteSpace(settings.ProjectId) ||
            string.IsNullOrWhiteSpace(secrets.ClientEmail) ||
            string.IsNullOrWhiteSpace(secrets.PrivateKey))
        {
            throw new global::Notifications.Application.Contracts.Delivery.NotificationChannelUnavailableException("provider_configuration_unavailable",
                $"FCM configuration '{config.ConfigId}' is missing ProjectId, ClientEmail, or PrivateKey.");
        }

        string accessToken;
        try
        {
            accessToken = await _accessTokenProvider.GetAccessTokenAsync(secrets.ClientEmail, secrets.PrivateKey, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or CryptographicException or FormatException)
        {
            _logger.LogWarning("FCM access token exchange failed for configuration {ConfigId}.", config.ConfigId);
            return NotificationProviderSendResult.Unavailable("fcm_auth_failed");
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

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var responseBody = await NotificationHttpResponseOutcome.ReadBodyAsync(response.Content, cancellationToken);
        if (responseBody is null)
            return NotificationProviderSendResult.Ambiguous("provider_response_too_large");

        if (response.IsSuccessStatusCode)
        {
            var messageName = TryReadMessageName(responseBody);
            return string.IsNullOrWhiteSpace(messageName) ? NotificationProviderSendResult.Ambiguous("fcm_acceptance_id_missing") : NotificationProviderSendResult.Success(messageName);
        }

        var status = TryReadErrorStatus(responseBody, (int)response.StatusCode);
        var errorCode = status ?? ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture);
        _logger.LogWarning(
            "FCM push send failed. HttpStatus={HttpStatus}, FcmStatus={FcmStatus}.",
            (int)response.StatusCode,
            errorCode);
        var documentedServerRejection = ((int)response.StatusCode, status) is (500, "INTERNAL") or (503, "UNAVAILABLE");
        return NotificationHttpResponseOutcome.Rejection(response, $"fcm_{errorCode}", TimeSpan.FromMinutes(1), documentedServerRejection);
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
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                ? name.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TryReadErrorStatus(string json, int httpStatus)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object &&
                error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number &&
                code.TryGetInt32(out var reportedCode) && reportedCode == httpStatus &&
                error.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String)
            {
                return status.GetString() switch
                {
                    "INVALID_ARGUMENT" or "UNAUTHENTICATED" or "PERMISSION_DENIED" or "NOT_FOUND" or
                    "RESOURCE_EXHAUSTED" or "INTERNAL" or "UNAVAILABLE" => status.GetString(),
                    _ => null
                };
            }
        }
        catch (JsonException)
        {
            // Non-JSON body; nothing to extract.
        }

        return null;
    }
}
