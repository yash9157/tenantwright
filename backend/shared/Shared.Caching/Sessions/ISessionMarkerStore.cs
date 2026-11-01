namespace Shared.Caching.Sessions;

public interface ISessionMarkerStore
{
    Task SetActiveAsync(
        Guid? tenantId,
        Guid userId,
        string sessionId,
        string value,
        TimeSpan ttl,
        CancellationToken cancellationToken = default);

    Task<string?> IsActiveAsync(
        Guid? tenantId,
        Guid userId,
        string sessionId,
        CancellationToken cancellationToken = default);

    Task RemoveAsync(
        Guid? tenantId,
        Guid userId,
        string sessionId,
        CancellationToken cancellationToken = default);
}
