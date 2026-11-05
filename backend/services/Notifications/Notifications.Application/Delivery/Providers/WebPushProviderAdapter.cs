using Notifications.Domain.Shared.Enums;
using System.Net;
using System.Text.Json;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;
using Microsoft.Extensions.Logging;
using Notifications.Application.Contracts.Channels;
using Notifications.Application.Contracts.ProviderConfig;
using Shared.Modularity.DependencyInjection;

namespace Notifications.Application.Delivery.Providers;

/// <summary>Standards-based Web Push adapter using encrypted browser PushSubscription material.</summary>
[DisableConventionalRegistration]
public sealed class WebPushProviderAdapter : IPushProviderAdapter, ITransientDependency
{
    public const string HttpClientName = "Notifications.WebPush";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<WebPushProviderAdapter> _logger;

    public WebPushProviderAdapter(IHttpClientFactory httpClientFactory, ILogger<WebPushProviderAdapter> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public string ProviderName => "WebPush";

    public async Task<NotificationProviderSendResult> SendAsync(string deviceToken, string title, string body, ResolvedNotificationProviderConfig config, CancellationToken cancellationToken = default)
    {
        using var sendActivity = NotificationProviderTrace.Start(NotificationChannel.Push);
        var settings = JsonSerializer.Deserialize<WebPushSettings>(config.SettingsJson, JsonOptions) ?? new();
        var secrets = JsonSerializer.Deserialize<WebPushSecrets>(config.Secret ?? "{}", JsonOptions) ?? new();
        var subscriptionPayload = JsonSerializer.Deserialize<WebPushSubscriptionPayload>(deviceToken, JsonOptions) ?? new();
        if (!Uri.TryCreate(subscriptionPayload.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(subscriptionPayload.P256dh) || string.IsNullOrWhiteSpace(subscriptionPayload.Auth) ||
            string.IsNullOrWhiteSpace(settings.Subject) || string.IsNullOrWhiteSpace(settings.PublicKey) || string.IsNullOrWhiteSpace(secrets.PrivateKey))
            throw new global::Notifications.Application.Contracts.Delivery.NotificationChannelUnavailableException("webpush_configuration_unavailable", "Web Push configuration or subscription is incomplete.");

        var subscription = new PushSubscription { Endpoint = endpoint.AbsoluteUri };
        subscription.SetKey(PushEncryptionKeyName.P256DH, subscriptionPayload.P256dh);
        subscription.SetKey(PushEncryptionKeyName.Auth, subscriptionPayload.Auth);
        var message = new PushMessage(JsonSerializer.Serialize(new { title, body }, JsonOptions));
        var authentication = new VapidAuthentication(settings.PublicKey, secrets.PrivateKey)
        {
            Subject = settings.Subject
        };
        try
        {
            var client = new PushServiceClient(_httpClientFactory.CreateClient(HttpClientName));
            await client.RequestPushMessageDeliveryAsync(subscription, message, authentication, cancellationToken);
            return NotificationProviderSendResult.Success(null);
        }
        catch (PushServiceClientException ex) when (ex.StatusCode is HttpStatusCode.Gone or HttpStatusCode.NotFound)
        {
            _logger.LogWarning("Web Push subscription expired for configuration {ConfigId}.", config.ConfigId);
            return NotificationProviderSendResult.PermanentRejection("webpush_subscription_gone");
        }
        catch (PushServiceClientException ex)
        {
            _logger.LogWarning("Web Push delivery failed for configuration {ConfigId} with status {StatusCode}.", config.ConfigId, (int)ex.StatusCode);
            using var response = new HttpResponseMessage(ex.StatusCode);
            return NotificationHttpResponseOutcome.Rejection(response, $"webpush_{(int)ex.StatusCode}");
        }
    }
}
