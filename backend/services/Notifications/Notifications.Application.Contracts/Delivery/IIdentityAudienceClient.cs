namespace Notifications.Application.Contracts.Delivery;

/// <summary>
/// Internal (M2M) client that expands audience groups into concrete user ids via Identity.
/// Implementations are fail-closed: on error they throw so the schedule is retried, never returning
/// a partial audience.
/// </summary>
public interface IIdentityAudienceClient
{
    Task<IReadOnlyList<Guid>> GetUsersByRoleAsync(Guid tenantId, string roleName, int maxUsers, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Guid>> GetTenantUserIdsAsync(Guid tenantId, int maxUsers, CancellationToken cancellationToken = default);
}
