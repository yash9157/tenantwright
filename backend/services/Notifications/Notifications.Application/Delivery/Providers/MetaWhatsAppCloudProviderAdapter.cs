using Notifications.Domain.Shared.Enums;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Notifications.Application.Contracts.Channels;
using Notifications.Application.Contracts.ProviderConfig;
using Shared.Modularity.DependencyInjection;

namespace Notifications.Application.Delivery.Providers;

/// <summary>
/// Sends WhatsApp messages through Meta's Cloud API. Only sends approved-template messages
/// (never free-form) — the requested template must be in the resolved config's
/// ApprovedTemplateNames, or the send fails closed. The access token is never logged.
/// </summary>
public sealed class MetaWhatsAppCloudProviderAdapter : IWhatsAppProviderAdapter, ITransientDependency
{
    public const string HttpClientName = "Notifications.Meta.WhatsApp";
    private const string GraphApiVersion = "v20.0";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<MetaWhatsAppCloudProviderAdapter> _logger;

    public MetaWhatsAppCloudProviderAdapter(
        IHttpClientFactory httpClientFactory,
        ILogger<MetaWhatsAppCloudProviderAdapter> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public string ProviderName => "MetaWhatsAppCloud";

    public async Task<NotificationProviderSendResult> SendAsync(
        string toPhoneNumber,
        string templateName,
        string bodyText,
        ResolvedNotificationProviderConfig config,
        CancellationToken cancellationToken = default)
    {
        using var sendActivity = NotificationProviderTrace.Start(NotificationChannel.WhatsApp);
        var settings = JsonSerializer.Deserialize<MetaWhatsAppCloudSettings>(config.SettingsJson, JsonOptions)
            ?? new MetaWhatsAppCloudSettings();
        var secrets = DeserializeSecrets(config.Secret);

        if (string.IsNullOrWhiteSpace(settings.PhoneNumberId) || string.IsNullOrWhiteSpace(secrets.AccessToken))
        {
            throw new global::Notifications.Application.Contracts.Delivery.NotificationChannelUnavailableException("provider_configuration_unavailable",
                $"Meta WhatsApp Cloud configuration '{config.ConfigId}' is missing PhoneNumberId or AccessToken.");
        }

        if (!settings.ApprovedTemplateNames.Contains(templateName, StringComparer.OrdinalIgnoreCase))
        {
            // Fail safe: never fall back to a free-form message for an unapproved template.
            return NotificationProviderSendResult.PermanentRejection("whatsapp_template_not_approved");
        }

        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://graph.facebook.com/{GraphApiVersion}/{Uri.EscapeDataString(settings.PhoneNumberId)}/messages");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secrets.AccessToken);

        var payload = new
        {
            messaging_product = "whatsapp",
            to = toPhoneNumber,
            type = "template",
            template = new
            {
                name = templateName,
                language = new { code = string.IsNullOrWhiteSpace(settings.TemplateLanguageCode) ? "en_US" : settings.TemplateLanguageCode },
                components = new object[]
                {
                    new
                    {
                        type = "body",
                        parameters = new object[] { new { type = "text", text = bodyText } }
                    }
                }
            }
        };
        request.Content = JsonContent.Create(payload, options: JsonOptions);

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var responseBody = await NotificationHttpResponseOutcome.ReadBodyAsync(response.Content, cancellationToken);
        if (responseBody is null)
            return NotificationProviderSendResult.Ambiguous("provider_response_too_large");

        if (response.IsSuccessStatusCode)
        {
            var messageId = TryReadMessageId(responseBody);
            return string.IsNullOrWhiteSpace(messageId) ? NotificationProviderSendResult.Ambiguous("whatsapp_acceptance_id_missing") : NotificationProviderSendResult.Success(messageId);
        }

        var errorCode = TryReadErrorCode(responseBody) ?? ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture);
        _logger.LogWarning(
            "Meta WhatsApp send failed. HttpStatus={HttpStatus}, MetaCode={MetaCode}.",
            (int)response.StatusCode,
            errorCode);
        return NotificationHttpResponseOutcome.Rejection(response, $"whatsapp_{errorCode}");
    }

    private static MetaWhatsAppCloudSecrets DeserializeSecrets(string? secretJson)
    {
        if (string.IsNullOrWhiteSpace(secretJson))
        {
            return new MetaWhatsAppCloudSecrets();
        }

        try
        {
            return JsonSerializer.Deserialize<MetaWhatsAppCloudSecrets>(secretJson, JsonOptions)
                ?? new MetaWhatsAppCloudSecrets();
        }
        catch (JsonException)
        {
            return new MetaWhatsAppCloudSecrets();
        }
    }

    private static string? TryReadMessageId(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("messages", out var messages) &&
                messages.ValueKind == JsonValueKind.Array &&
                messages.GetArrayLength() > 0 &&
                messages[0].ValueKind == JsonValueKind.Object &&
                messages[0].TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            {
                return id.GetString();
            }
        }
        catch (JsonException)
        {
            // Non-JSON or unexpected shape; nothing to extract.
        }

        return null;
    }

    private static string? TryReadErrorCode(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var code))
            {
                var value = code.ValueKind switch
                {
                    JsonValueKind.String => code.GetString(),
                    JsonValueKind.Number => code.GetRawText(),
                    _ => null
                };
                return NotificationHttpResponseOutcome.NumericProviderCode(value);
            }
        }
        catch (JsonException)
        {
            // Non-JSON body; nothing to extract.
        }

        return null;
    }
}
