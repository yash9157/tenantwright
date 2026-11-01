using System.Diagnostics.Metrics;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Notifications.Application.Contracts.Delivery;
using Notifications.Application.Delivery;

namespace Notifications.Controllers.Webhooks;

/// <summary>
/// Generic transactional-email delivery-feedback callback (delivered/bounce/complaint). Anonymous
/// (providers have no bearer token) but authenticated by the configured signature scheme before any
/// parsing/mutation; rate-limited; host-context; idempotent by provider event id. Tenant/config are
/// derived from the matched delivery, never from the request body. Disabled until a provider is
/// configured. The concrete provider adapter (its exact signature/parse rules) is wired via the
/// pluggable <see cref="IEmailEventSignatureValidator"/>/<see cref="IEmailEventParser"/> seam.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/notifications/webhooks/{provider}/email")]
[EnableRateLimiting(EmailFeedbackWebhookController.RateLimiterPolicy)]
public sealed class EmailFeedbackWebhookController : ControllerBase
{
    public const string RateLimiterPolicy = "NotificationsEmailFeedbackWebhook";

    private static readonly Meter Meter = new("Notifications.Delivery");
    private static readonly Counter<long> AcceptedCounter = Meter.CreateCounter<long>("notifications.email.feedback.accepted");
    private static readonly Counter<long> DuplicateCounter = Meter.CreateCounter<long>("notifications.email.feedback.duplicate");
    private static readonly Counter<long> UnmatchedCounter = Meter.CreateCounter<long>("notifications.email.feedback.unmatched");
    private static readonly Counter<long> SignatureRejectedCounter = Meter.CreateCounter<long>("notifications.email.feedback.signature_rejected");
    private static readonly Counter<long> EventCounter = Meter.CreateCounter<long>("notifications.email.feedback.event");

    private readonly NotificationsEmailFeedbackOptions _options;
    private readonly IEmailEventSignatureValidator _signatureValidator;
    private readonly IEmailEventParser _parser;
    private readonly IEmailDeliveryFeedbackService _feedbackService;
    private readonly NotificationDatabaseLocator _databaseLocator;

    public EmailFeedbackWebhookController(
        IOptions<NotificationsEmailFeedbackOptions> options,
        IEmailEventSignatureValidator signatureValidator,
        IEmailEventParser parser,
        IEmailDeliveryFeedbackService feedbackService,
        NotificationDatabaseLocator databaseLocator)
    {
        _options = options.Value;
        _signatureValidator = signatureValidator;
        _parser = parser;
        _feedbackService = feedbackService;
        _databaseLocator = databaseLocator;
    }

    [HttpPost]
    public async Task<IActionResult> HandleAsync(string provider, CancellationToken cancellationToken)
    {
        // Feature off, or a provider we are not configured for: do not reveal anything.
        if (!_options.Enabled ||
            string.IsNullOrWhiteSpace(provider) ||
            (!string.IsNullOrWhiteSpace(_options.Provider) &&
             !string.Equals(provider, _options.Provider, StringComparison.OrdinalIgnoreCase)))
        {
            return NotFound();
        }

        if (Request.ContentLength is long length && length > _options.MaxBodyBytes)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var rawBody = await ReadBoundedBodyAsync(cancellationToken);
        if (rawBody == null)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var request = new EmailWebhookRequest
        {
            Provider = provider,
            RawBody = rawBody,
            Headers = Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase)
        };

        if (!_signatureValidator.IsValid(request))
        {
            SignatureRejectedCounter.Add(1);
            return Unauthorized();
        }

        var events = _parser.Parse(request);
        if (events.Count == 0)
        {
            return Ok();
        }

        var processed = 0;
        foreach (var feedbackEvent in events)
        {
            if (processed >= _options.MaxEvents)
            {
                break;
            }

            processed++;
            EmailFeedbackRecordResult result;
            // Each event is correlated in the database that owns its message (a dedicated tenant's own database).
            using (_databaseLocator.Enter(await _databaseLocator.FindProviderMessageDatabaseAsync(feedbackEvent.ProviderMessageId, cancellationToken)))
            {
                result = await _feedbackService.RecordAsync(provider, feedbackEvent, cancellationToken);
            }

            switch (result)
            {
                case EmailFeedbackRecordResult.Accepted:
                    AcceptedCounter.Add(1);
                    EventCounter.Add(1, new KeyValuePair<string, object?>("type", feedbackEvent.EventType.ToString().ToLowerInvariant()));
                    break;
                case EmailFeedbackRecordResult.Duplicate:
                    DuplicateCounter.Add(1);
                    break;
                case EmailFeedbackRecordResult.Unmatched:
                    UnmatchedCounter.Add(1);
                    break;
            }
        }

        return Ok();
    }

    private async Task<string?> ReadBoundedBodyAsync(CancellationToken cancellationToken)
    {
        var limit = Math.Max(1, _options.MaxBodyBytes);
        using var reader = new StreamReader(Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var buffer = new char[4096];
        var builder = new StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer, cancellationToken)) > 0)
        {
            builder.Append(buffer, 0, read);
            if (builder.Length > limit)
            {
                return null;
            }
        }

        return builder.ToString();
    }
}
