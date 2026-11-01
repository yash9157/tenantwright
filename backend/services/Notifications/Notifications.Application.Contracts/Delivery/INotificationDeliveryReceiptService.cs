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
    /// Applies a provider delivery receipt to the matching delivery (found by provider message id).
    /// Runs host-context (no ambient tenant). Idempotent: re-applying the same terminal status is a
    /// no-op. Returns true when a matching delivery was found.
    /// </summary>
    Task<bool> ApplyProviderStatusAsync(
        string providerMessageId,
        NotificationChannel channel,
        NotificationDeliveryReceiptStatus status,
        string? providerErrorCode,
        CancellationToken cancellationToken = default);
}
