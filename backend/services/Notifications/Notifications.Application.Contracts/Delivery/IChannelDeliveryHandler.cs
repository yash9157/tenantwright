using Notifications.Application.Contracts.Channels;
using Notifications.Domain.Shared.Enums;

namespace Notifications.Application.Contracts.Delivery;

public interface IChannelDeliveryHandler
{
    NotificationChannel Channel { get; }

    Task<ChannelDeliveryResult> DeliverAsync(
        ChannelDeliveryContext context,
        CancellationToken cancellationToken = default);
}

public sealed class ChannelDeliveryContext
{
    public required Guid? TenantId { get; init; }
    public required Guid NotificationId { get; init; }
    public required Guid DeliveryAttemptId { get; init; }
    public required string TemplateName { get; init; }
    public required string? Subject { get; init; }
    public required NotificationRecipientType RecipientType { get; init; }
    public required string RecipientValue { get; init; }
    public required IReadOnlyDictionary<string, string?> TemplateModel { get; init; }
}

public sealed class ChannelDeliveryResult
{
    public required NotificationSendOutcome Outcome { get; init; }
    public bool IsSuccess => Outcome is NotificationSendOutcome.Accepted or NotificationSendOutcome.Delivered;
    public bool IsUnavailable => Outcome == NotificationSendOutcome.UnavailableBeforeSend;
    public string? ProviderMessageId { get; init; }
    public Guid? ProviderConfigId { get; init; }
    public string? ErrorCode { get; init; }
    public string? UserId { get; init; }
    public DateTime? RetryAfterUtc { get; init; }

    public static ChannelDeliveryResult Success(string? providerMessageId = null, string? userId = null, Guid? providerConfigId = null) =>
        new() { Outcome = NotificationSendOutcome.Accepted, ProviderMessageId = providerMessageId, UserId = userId, ProviderConfigId = providerConfigId };
    public static ChannelDeliveryResult FromProvider(NotificationProviderSendResult result) => new()
    {
        Outcome = result.Outcome,
        ProviderMessageId = result.ProviderMessageId,
        ProviderConfigId = result.ProviderConfigId,
        ErrorCode = result.ErrorCode,
        RetryAfterUtc = result.RetryAfterUtc
    };
    public static ChannelDeliveryResult PermanentRejection(string code) => new() { Outcome = NotificationSendOutcome.PermanentRejection, ErrorCode = code };
    public static ChannelDeliveryResult RetryableRejection(string code, DateTime? retryAfterUtc = null) =>
        new() { Outcome = NotificationSendOutcome.RetryableRejection, ErrorCode = code, RetryAfterUtc = retryAfterUtc };
    public static ChannelDeliveryResult Unavailable(string code) => new() { Outcome = NotificationSendOutcome.UnavailableBeforeSend, ErrorCode = code };
    public static ChannelDeliveryResult Ambiguous(string code) => new() { Outcome = NotificationSendOutcome.Ambiguous, ErrorCode = code };
}
