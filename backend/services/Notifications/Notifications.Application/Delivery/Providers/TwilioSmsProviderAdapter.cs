using Notifications.Domain.Shared.Enums;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Notifications.Application.Contracts.Channels;
using Notifications.Application.Contracts.ProviderConfig;
using Shared.Modularity.DependencyInjection;

namespace Notifications.Application.Delivery.Providers;

/// <summary>
/// Sends SMS through Twilio's Messages API using a named <see cref="IHttpClientFactory"/> client.
/// Account SID/from-number come from the resolved config's non-secret settings; the auth token
/// comes from the resolved config's decrypted secret and is never logged.
/// </summary>
public sealed class TwilioSmsProviderAdapter : ISmsProviderAdapter, ITransientDependency
{
    public const string HttpClientName = "Notifications.Twilio.Sms";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TwilioSmsProviderAdapter> _logger;

    public TwilioSmsProviderAdapter(
        IHttpClientFactory httpClientFactory,
        ILogger<TwilioSmsProviderAdapter> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public string ProviderName => "Twilio";

    public async Task<NotificationProviderSendResult> SendAsync(
        string toPhoneNumber,
        string message,
        ResolvedNotificationProviderConfig config,
        CancellationToken cancellationToken = default)
    {
        using var sendActivity = NotificationProviderTrace.Start(NotificationChannel.Sms);
        var settings = JsonSerializer.Deserialize<TwilioSmsSettings>(config.SettingsJson, JsonOptions) ?? new TwilioSmsSettings();
        var authToken = config.Secret;

        if (string.IsNullOrWhiteSpace(settings.AccountSid) ||
            string.IsNullOrWhiteSpace(authToken) ||
            string.IsNullOrWhiteSpace(settings.FromNumber))
        {
            throw new global::Notifications.Application.Contracts.Delivery.NotificationChannelUnavailableException("provider_configuration_unavailable",
                $"Twilio SMS provider configuration '{config.ConfigId}' is missing AccountSid, AuthToken, or FromNumber.");
        }

        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://api.twilio.com/2010-04-01/Accounts/{Uri.EscapeDataString(settings.AccountSid)}/Messages.json");

        var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{settings.AccountSid}:{authToken}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["To"] = toPhoneNumber,
            ["From"] = settings.FromNumber,
            ["Body"] = message
        });

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var payload = await NotificationHttpResponseOutcome.ReadBodyAsync(response.Content, cancellationToken);
        if (payload is null)
            return NotificationProviderSendResult.Ambiguous("provider_response_too_large");

        if (response.IsSuccessStatusCode)
        {
            var sid = TryReadValue(payload, "sid", allowNumber: false);
            return string.IsNullOrWhiteSpace(sid) ? NotificationProviderSendResult.Ambiguous("twilio_acceptance_id_missing") : NotificationProviderSendResult.Success(sid);
        }

        var errorCode = NotificationHttpResponseOutcome.NumericProviderCode(TryReadValue(payload, "code", allowNumber: true))
            ?? ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture);
        _logger.LogWarning(
            "Twilio SMS send failed. HttpStatus={HttpStatus}, TwilioCode={TwilioCode}.",
            (int)response.StatusCode,
            errorCode);
        return NotificationHttpResponseOutcome.Rejection(response, $"twilio_{errorCode}");
    }

    private static string? TryReadValue(string json, string property, bool allowNumber)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty(property, out var value))
            {
                return value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString(),
                    JsonValueKind.Number when allowNumber => value.GetRawText(),
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
