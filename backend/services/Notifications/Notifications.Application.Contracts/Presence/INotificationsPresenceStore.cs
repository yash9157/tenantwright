namespace Notifications.Application.Contracts.Presence;

public interface INotificationsPresenceStore
{
    Task SetActiveAsync(Guid? tenantId, string userId, CancellationToken cancellationToken = default);
    Task<bool> IsActiveAsync(Guid? tenantId, string userId, CancellationToken cancellationToken = default);
}
