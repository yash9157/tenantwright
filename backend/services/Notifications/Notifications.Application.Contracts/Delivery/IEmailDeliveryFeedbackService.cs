using Notifications.Domain.Shared.Enums;

namespace Notifications.Application.Contracts.Delivery;

/// <summary>
/// A single normalized email delivery-feedback event parsed from a provider webhook payload.
/// Carries only safe, bounded fields; never a recipient address or raw payload.
/// </summary>
public sealed class EmailDeliveryEvent
{
    /// <summary>Provider's stable event id (idempotency key). Required.</summary>
    public required string ProviderEventId { get; init; }

    /// <summary>Provider message id that correlates to the sent delivery's <c>ProviderMessageId</c>. Required.</summary>
    public required string ProviderMessageId { get; init; }

    public required EmailDeliveryEventType EventType { get; init; }

    public string? ProviderCode { get; init; }

    public string? SafeReason { get; init; }

    public DateTime? OccurredAtUtc { get; init; }
}

/// <summary>Outcome of recording a single email feedback event.</summary>
public enum EmailFeedbackRecordResult
{
    /// <summary>Recorded a new receipt fact.</summary>
    Accepted = 1,

    /// <summary>A receipt with the same provider/event id already existed; no-op.</summary>
    Duplicate = 2,

    /// <summary>No email delivery matched the provider message id; nothing recorded.</summary>
    Unmatched = 3
}

/// <summary>
/// Records asynchronous email delivery-feedback events (delivered/bounce/complaint) against the
/// matching email delivery. Runs host-context from an anonymous, signature-verified webhook.
/// Idempotent by (provider, provider event id). A hard bounce/complaint transitions a previously
/// <c>Sent</c> delivery to <c>DeadLettered</c>; a delivered event is informational.
/// </summary>
public interface IEmailDeliveryFeedbackService
{
    Task<EmailFeedbackRecordResult> RecordAsync(
        string providerName,
        EmailDeliveryEvent feedbackEvent,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Validates the signature of an inbound provider webhook request before any parsing/mutation.
/// Implementations must be constant-time and reject unknown/invalid signatures generically.
/// </summary>
public interface IEmailEventSignatureValidator
{
    bool IsValid(EmailWebhookRequest request);
}

/// <summary>Parses a validated provider webhook body into normalized <see cref="EmailDeliveryEvent"/> items.</summary>
public interface IEmailEventParser
{
    IReadOnlyList<EmailDeliveryEvent> Parse(EmailWebhookRequest request);
}

/// <summary>The raw, size-bounded inbound webhook request captured for validation and parsing.</summary>
public sealed class EmailWebhookRequest
{
    public required string Provider { get; init; }

    public required string RawBody { get; init; }

    public required IReadOnlyDictionary<string, string> Headers { get; init; }

    public DateTime ReceivedAtUtc { get; init; } = DateTime.UtcNow;
}
