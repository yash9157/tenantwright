namespace Notifications.Application.Contracts.Delivery;

public interface IIdentityNotificationPreferenceClient
{
    Task<bool> IsNotificationEnabledAsync(
        Guid? tenantId,
        Guid userId,
        string preferenceKey,
        CancellationToken cancellationToken = default);
}
