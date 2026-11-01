using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Notifications.Application.Contracts.Channels;
using Notifications.Application.Contracts.Delivery;
using Notifications.Application.Contracts.ProviderConfig;
using Notifications.Application.Delivery;
using Notifications.Application.Delivery.Providers;
using Notifications.Domain.Shared.Enums;

namespace Notifications.Controllers.Webhooks;

/// <summary>
/// Meta WhatsApp Cloud API webhook: GET handles the one-time subscription verification handshake;
/// POST handles asynchronous delivery-status callbacks. Anonymous (Meta carries no bearer token) but
/// authenticated by <c>X-Hub-Signature-256</c>. The callback URL includes the active WhatsApp
/// configuration id, so verification and signature validation use that configuration's secret
/// without accepting another tenant's credentials.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/notifications/webhooks/meta/whatsapp/{configId:guid}")]
[EnableRateLimiting(MetaWhatsAppWebhookController.RateLimiterPolicy)]
public sealed class MetaWhatsAppWebhookController : ControllerBase
{
    public const string RateLimiterPolicy = "NotificationsMetaWhatsAppWebhook";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly INotificationProviderConfigResolver _resolver;
    private readonly INotificationDeliveryReceiptService _receiptService;
    private readonly NotificationDatabaseLocator _databaseLocator;

    public MetaWhatsAppWebhookController(
        INotificationProviderConfigResolver resolver,
        INotificationDeliveryReceiptService receiptService,
        NotificationDatabaseLocator databaseLocator)
    {
        _resolver = resolver;
        _receiptService = receiptService;
        _databaseLocator = databaseLocator;
    }

    [HttpGet]
    public async Task<IActionResult> VerifyAsync(
        Guid configId,
        [FromQuery(Name = "hub.mode")] string? mode,
        [FromQuery(Name = "hub.verify_token")] string? verifyToken,
        [FromQuery(Name = "hub.challenge")] string? challenge,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(mode, "subscribe", StringComparison.Ordinal) || string.IsNullOrEmpty(challenge))
        {
            return BadRequest();
        }

        using var configDatabase = _databaseLocator.Enter(await _databaseLocator.FindProviderConfigDatabaseAsync(configId, cancellationToken));
        var config = await _resolver.ResolveByConfigIdAsync(configId, NotificationChannel.WhatsApp, cancellationToken);
        var settings = config != null
            ? JsonSerializer.Deserialize<MetaWhatsAppCloudSettings>(config.SettingsJson, JsonOptions)
            : null;

        if (settings == null ||
            string.IsNullOrEmpty(settings.WebhookVerifyToken) ||
            !string.Equals(settings.WebhookVerifyToken, verifyToken, StringComparison.Ordinal))
        {
            return Forbid();
        }

        return Content(challenge, "text/plain");
    }

    [HttpPost]
    public async Task<IActionResult> HandleAsync(Guid configId, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var rawBody = await reader.ReadToEndAsync(cancellationToken);
        var rawBodyBytes = Encoding.UTF8.GetBytes(rawBody);

        // The configuration, and the deliveries it sent, live in the database of the tenant that owns it.
        using var configDatabase = _databaseLocator.Enter(await _databaseLocator.FindProviderConfigDatabaseAsync(configId, cancellationToken));
        var config = await _resolver.ResolveByConfigIdAsync(configId, NotificationChannel.WhatsApp, cancellationToken);
        var appSecret = config != null ? DeserializeAppSecret(config.Secret) : null;
        if (string.IsNullOrEmpty(appSecret))
        {
            // Provider not configured; reject so callbacks are not silently accepted unvalidated.
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        var signature = Request.Headers["X-Hub-Signature-256"].ToString();
        if (!MetaWebhookSignatureValidator.IsValid(appSecret, rawBodyBytes, signature))
        {
            return Unauthorized();
        }

        foreach (var status in ExtractStatuses(rawBody))
        {
            var mapped = MapStatus(status.Status);
            if (mapped.HasValue && !string.IsNullOrWhiteSpace(status.MessageId))
            {
                await _receiptService.ApplyProviderStatusAsync(
                    status.MessageId,
                    NotificationChannel.WhatsApp,
                    mapped.Value,
                    status.ErrorCode,
                    cancellationToken);
            }
        }

        return Ok();
    }

    private static string? DeserializeAppSecret(string? secretJson)
    {
        if (string.IsNullOrWhiteSpace(secretJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<MetaWhatsAppCloudSecrets>(secretJson, JsonOptions)?.AppSecret;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<(string MessageId, string? Status, string? ErrorCode)> ExtractStatuses(string rawBody)
    {
        var results = new List<(string, string?, string?)>();

        try
        {
            using var document = JsonDocument.Parse(rawBody);
            if (!document.RootElement.TryGetProperty("entry", out var entries) || entries.ValueKind != JsonValueKind.Array)
            {
                return results;
            }

            foreach (var entry in entries.EnumerateArray())
            {
                if (!entry.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var change in changes.EnumerateArray())
                {
                    if (!change.TryGetProperty("value", out var value) ||
                        !value.TryGetProperty("statuses", out var statuses) ||
                        statuses.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var status in statuses.EnumerateArray())
                    {
                        var messageId = status.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
                        if (string.IsNullOrWhiteSpace(messageId))
                        {
                            continue;
                        }

                        var statusValue = status.TryGetProperty("status", out var statusElement) ? statusElement.GetString() : null;
                        string? errorCode = null;
                        if (status.TryGetProperty("errors", out var errors) &&
                            errors.ValueKind == JsonValueKind.Array &&
                            errors.GetArrayLength() > 0 &&
                            errors[0].TryGetProperty("code", out var codeElement))
                        {
                            errorCode = codeElement.ValueKind == JsonValueKind.String ? codeElement.GetString() : codeElement.ToString();
                        }

                        results.Add((messageId, statusValue, errorCode));
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Malformed payload; nothing to process.
        }

        return results;
    }

    private static NotificationDeliveryReceiptStatus? MapStatus(string? status)
    {
        return status?.Trim().ToLowerInvariant() switch
        {
            "delivered" => NotificationDeliveryReceiptStatus.Delivered,
            "read" => NotificationDeliveryReceiptStatus.Delivered,
            "failed" => NotificationDeliveryReceiptStatus.Failed,
            _ => null
        };
    }
}
