using Notifications.Domain.Shared.Enums;

namespace Notifications.Application.Contracts.Delivery;

public enum NotificationDeliveryReceiptStatus
{
    Delivered = 1,
    Failed = 2
}

public interface INotificationDeliveryReceiptService
{
    /// <summary>
    /// Applies a verified receipt to the current attempt in the owning database. The channel and
    /// configuration must match the callback's authenticated provider identity. Reapplying the same
    /// terminal status is a no-op.
    /// </summary>
    Task<bool> ApplyProviderStatusAsync(
        string providerMessageId,
        NotificationChannel channel,
        Guid authenticatedProviderConfigId,
        NotificationDeliveryReceiptStatus status,
        string? providerErrorCode,
        CancellationToken cancellationToken = default);
}
