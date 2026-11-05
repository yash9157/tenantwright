using Notifications.Domain.Shared.Enums;

namespace Notifications.Application.Contracts.Channels;

public sealed record NotificationProviderSendResult
{
    private NotificationProviderSendResult(NotificationSendOutcome outcome, string? messageId = null,
        string? errorCode = null, DateTime? retryAfterUtc = null)
    {
        Outcome = outcome;
        ProviderMessageId = messageId;
        ErrorCode = errorCode;
        RetryAfterUtc = retryAfterUtc;
    }

    public NotificationSendOutcome Outcome { get; }
    public bool IsSuccess => Outcome is NotificationSendOutcome.Accepted or NotificationSendOutcome.Delivered;
    public string? ProviderMessageId { get; }
    public Guid? ProviderConfigId { get; private init; }
    public string? ErrorCode { get; }
    public DateTime? RetryAfterUtc { get; }

    public NotificationProviderSendResult WithConfiguration(Guid configId) => this with { ProviderConfigId = configId };
    public static NotificationProviderSendResult Success(string? messageId) => new(NotificationSendOutcome.Accepted, messageId);
    public static NotificationProviderSendResult PermanentRejection(string code) => new(NotificationSendOutcome.PermanentRejection, errorCode: code);
    public static NotificationProviderSendResult RetryableRejection(string code, DateTime? retryAfterUtc = null) =>
        new(NotificationSendOutcome.RetryableRejection, errorCode: code, retryAfterUtc: retryAfterUtc);
    public static NotificationProviderSendResult Unavailable(string code) => new(NotificationSendOutcome.UnavailableBeforeSend, errorCode: code);
    public static NotificationProviderSendResult Ambiguous(string code) => new(NotificationSendOutcome.Ambiguous, errorCode: code);
}
