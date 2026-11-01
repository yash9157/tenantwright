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

    public async Task<WhatsAppProviderSendResult> SendAsync(
        string toPhoneNumber,
        string templateName,
        string bodyText,
        ResolvedNotificationProviderConfig config,
        CancellationToken cancellationToken = default)
    {
        var settings = JsonSerializer.Deserialize<MetaWhatsAppCloudSettings>(config.SettingsJson, JsonOptions)
            ?? new MetaWhatsAppCloudSettings();
        var secrets = DeserializeSecrets(config.Secret);

        if (string.IsNullOrWhiteSpace(settings.PhoneNumberId) || string.IsNullOrWhiteSpace(secrets.AccessToken))
        {
            throw new InvalidOperationException(
                $"Meta WhatsApp Cloud configuration '{config.ConfigId}' is missing PhoneNumberId or AccessToken.");
        }

        if (!settings.ApprovedTemplateNames.Contains(templateName, StringComparer.OrdinalIgnoreCase))
        {
            // Fail safe: never fall back to a free-form message for an unapproved template.
            return WhatsAppProviderSendResult.Failed("whatsapp_template_not_approved");
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

        using var response = await client.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            var messageId = TryReadMessageId(responseBody);
            return WhatsAppProviderSendResult.Success(messageId);
        }

        var errorCode = TryReadErrorCode(responseBody) ?? ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture);
        // Meta's error body can echo the recipient number but never the access token; log only the
        // vendor error code and HTTP status.
        _logger.LogWarning(
            "Meta WhatsApp send failed. HttpStatus={HttpStatus}, MetaCode={MetaCode}.",
            (int)response.StatusCode,
            errorCode);
        return WhatsAppProviderSendResult.Failed($"whatsapp_{errorCode}");
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
            if (document.RootElement.TryGetProperty("messages", out var messages) &&
                messages.ValueKind == JsonValueKind.Array &&
                messages.GetArrayLength() > 0 &&
                messages[0].TryGetProperty("id", out var id))
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
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                if (error.TryGetProperty("code", out var code))
                {
                    return code.ValueKind == JsonValueKind.String ? code.GetString() : code.ToString();
                }
            }
        }
        catch (JsonException)
        {
            // Non-JSON body; nothing to extract.
        }

        return null;
    }
}
