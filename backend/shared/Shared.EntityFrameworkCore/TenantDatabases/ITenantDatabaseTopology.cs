namespace Shared.EntityFrameworkCore.TenantDatabases;

/// <summary>
/// Yields the distinct databases a service owns for one connection-string name: the shared (host) database plus
/// every active tenant's dedicated database, deduplicated by normalized (host, port, database) target.
/// <para>
/// Cross-tenant background work (outbox dispatch, inbox retention, sweeps, workers, migrations) must iterate this
/// set instead of reading only the host database, because rows written in a dedicated tenant's scope live in that
/// tenant's database. Names that are host-only in <c>TenantWrightDatabaseCatalog</c>, and multi-tenancy-unaware
/// DbContexts, always yield the shared database alone.
/// </para>
/// </summary>
public interface ITenantDatabaseTopology
{
    /// <summary>
    /// The current snapshot for <paramref name="connectionStringName"/>, cached in-process for
    /// <c>Shared:Data:ConnectionStringResolver:TenantCacheSeconds</c>.
    /// </summary>
    Task<TenantDatabaseTopologySnapshot> GetSnapshotAsync(
        string connectionStringName,
        CancellationToken cancellationToken = default);

    /// <summary>The snapshot for the connection-string name and tenancy mode of <paramref name="dbContextType"/>.</summary>
    Task<TenantDatabaseTopologySnapshot> GetSnapshotAsync(
        Type dbContextType,
        CancellationToken cancellationToken = default);

    /// <summary>Drops every cached snapshot so the next read recomputes from the tenant store.</summary>
    void Invalidate();
}
