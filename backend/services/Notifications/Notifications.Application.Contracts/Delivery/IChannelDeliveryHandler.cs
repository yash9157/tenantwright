using Notifications.Domain.Shared.Constants;
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
    public bool IsSuccess { get; init; }
    public bool IsUnavailable { get; init; }
    public string? ProviderMessageId { get; init; }
    public Guid? ProviderConfigId { get; init; }
    public string? ErrorCode { get; init; }
    public string? UserId { get; init; }

    public static ChannelDeliveryResult Success(string? providerMessageId = null, string? userId = null, Guid? providerConfigId = null)
    {
        return new ChannelDeliveryResult
        {
            IsSuccess = true,
            ProviderMessageId = providerMessageId,
            ProviderConfigId = providerConfigId,
            UserId = userId
        };
    }

    public static ChannelDeliveryResult Failed(string errorCode)
    {
        return new ChannelDeliveryResult
        {
            IsSuccess = false,
            ErrorCode = string.IsNullOrWhiteSpace(errorCode)
                ? "delivery_failed"
                : errorCode.Trim()
        };
    }

    public static ChannelDeliveryResult Unavailable(string errorCode)
    {
        return new ChannelDeliveryResult
        {
            IsSuccess = false,
            IsUnavailable = true,
            ErrorCode = string.IsNullOrWhiteSpace(errorCode)
                ? "channel_unavailable"
                : errorCode.Trim()
        };
    }
}
