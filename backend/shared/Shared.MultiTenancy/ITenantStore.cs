namespace Shared.MultiTenancy;

public interface ITenantStore
{
    [Obsolete("Use FindAsync(Guid, CancellationToken) for non-blocking tenant resolution.")]
    TenantConfiguration? Find(Guid id);

    [Obsolete("Use FindAsync(string, CancellationToken) for non-blocking tenant resolution.")]
    TenantConfiguration? Find(string normalizedName);

    [Obsolete("Use GetListAsync(CancellationToken) for non-blocking tenant resolution.")]
    IReadOnlyList<TenantConfiguration> GetList();

    Task<TenantConfiguration?> FindAsync(Guid id, CancellationToken cancellationToken = default);
    Task<TenantConfiguration?> FindAsync(string normalizedName, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TenantConfiguration>> GetListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The tenant list when it is known to be current, or <see langword="null"/> when it is unavailable (absent or
    /// stale). Unlike <see cref="GetListAsync"/>, which returns an empty list in both cases, callers that must not
    /// mistake a cold cache for "no tenants" (database fan-out, migrations) can tell the two apart.
    /// </summary>
    Task<IReadOnlyList<TenantConfiguration>?> FindListAsync(CancellationToken cancellationToken = default);
}
