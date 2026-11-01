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
/// Twilio SMS status callback. Anonymous (Twilio has no bearer token) but authenticated by Twilio's
/// request signature; rate-limited; idempotent by provider message id. Updates the matching delivery.
/// The signing secret is resolved per-message from the scoped provider configuration that sent it
/// (tenant-owned or host-default), since different tenants may use different Twilio accounts.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/notifications/webhooks/twilio/sms")]
[EnableRateLimiting(TwilioSmsWebhookController.RateLimiterPolicy)]
public sealed class TwilioSmsWebhookController : ControllerBase
{
    public const string RateLimiterPolicy = "NotificationsTwilioWebhook";

    private readonly INotificationProviderConfigResolver _resolver;
    private readonly INotificationDeliveryReceiptService _receiptService;
    private readonly NotificationDatabaseLocator _databaseLocator;

    public TwilioSmsWebhookController(
        INotificationProviderConfigResolver resolver,
        INotificationDeliveryReceiptService receiptService,
        NotificationDatabaseLocator databaseLocator)
    {
        _resolver = resolver;
        _receiptService = receiptService;
        _databaseLocator = databaseLocator;
    }

    [HttpPost]
    public async Task<IActionResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (!Request.HasFormContentType)
        {
            return BadRequest();
        }

        var form = await Request.ReadFormAsync(cancellationToken);
        var messageSid = form["MessageSid"].ToString();
        if (string.IsNullOrWhiteSpace(messageSid))
        {
            return BadRequest();
        }

        // Correlate in the database that owns the message: a dedicated tenant's deliveries live in its own database.
        using var owningDatabase = _databaseLocator.Enter(await _databaseLocator.FindProviderMessageDatabaseAsync(messageSid, cancellationToken));
        var config = await _resolver.ResolveByProviderMessageIdAsync(messageSid, NotificationChannel.Sms, cancellationToken);
        var authToken = config?.Secret;
        if (string.IsNullOrWhiteSpace(authToken))
        {
            // Unknown message id or provider not configured; reject so callbacks are never accepted unvalidated.
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        var formPairs = form.Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value.ToString()));
        var signature = Request.Headers["X-Twilio-Signature"].ToString();

        if (!TwilioSignatureValidator.IsValid(authToken, BuildRequestUrl(config!), formPairs, signature))
        {
            return Unauthorized();
        }

        var messageStatus = form["MessageStatus"].ToString();
        var errorCode = form["ErrorCode"].ToString();

        var mapped = MapStatus(messageStatus);
        if (mapped.HasValue)
        {
            await _receiptService.ApplyProviderStatusAsync(
                messageSid,
                NotificationChannel.Sms,
                mapped.Value,
                string.IsNullOrWhiteSpace(errorCode) ? null : errorCode,
                cancellationToken);
        }

        return Ok();
    }

    private string BuildRequestUrl(ResolvedNotificationProviderConfig config)
    {
        var settings = JsonSerializer.Deserialize<TwilioSmsSettings>(config.SettingsJson);
        if (!string.IsNullOrWhiteSpace(settings?.WebhookBaseUrl))
        {
            return $"{settings.WebhookBaseUrl.TrimEnd('/')}{Request.PathBase}{Request.Path}{Request.QueryString}";
        }

        return $"{Request.Scheme}://{Request.Host}{Request.PathBase}{Request.Path}{Request.QueryString}";
    }

    private static NotificationDeliveryReceiptStatus? MapStatus(string? status)
    {
        return status?.Trim().ToLowerInvariant() switch
        {
            "delivered" => NotificationDeliveryReceiptStatus.Delivered,
            "failed" => NotificationDeliveryReceiptStatus.Failed,
            "undelivered" => NotificationDeliveryReceiptStatus.Failed,
            _ => null
        };
    }
}
