using Notifications.Application.Delivery;
using Notifications.Application.Digests;
using Notifications.Domain.Notifications;

namespace Notifications.Tests;

internal sealed class NeverSuppressedTenantNotificationChecker : ITenantNotificationSuppressionChecker
{
    public Task<bool> IsSuppressedAsync(Guid tenantId, CancellationToken cancellationToken = default)
        => Task.FromResult(false);
}

internal sealed class NoopNotificationDigestPlanner : INotificationDigestPlanner
{
    public Task<bool> TryAddAsync(
        Notification notification,
        NotificationRecipient recipient,
        CancellationToken cancellationToken)
        => Task.FromResult(false);
}
