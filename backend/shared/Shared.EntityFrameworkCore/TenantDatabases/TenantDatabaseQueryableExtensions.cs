using Shared.Domain;

namespace Shared.EntityFrameworkCore.TenantDatabases;

public static class TenantDatabaseQueryableExtensions
{
    /// <summary>
    /// Restricts a cross-tenant query (typically one that ignores the tenant filter) to the rows
    /// <paramref name="scope"/>'s database owns: see <see cref="TenantDatabaseScope.Owns"/>.
    /// </summary>
    public static IQueryable<TEntity> WhereOwnedBy<TEntity>(this IQueryable<TEntity> query, TenantDatabaseScope scope)
        where TEntity : IMultiTenant
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(scope);

        if (!scope.Database.IsShared)
        {
            var tenantId = scope.Database.TenantId;
            return query.Where(entity => entity.TenantId == tenantId);
        }

        var notOwned = scope.Topology.TenantIdsWithDedicatedAssignment.ToArray();
        return notOwned.Length == 0
            ? query
            : query.Where(entity => entity.TenantId == null || !notOwned.Contains(entity.TenantId.Value));
    }

    /// <summary>
    /// <see cref="WhereOwnedBy{TEntity}"/> for the database <see cref="TenantDatabaseFanOut"/> is visiting. Outside
    /// fan-out (a single-database caller) the query is returned unchanged.
    /// </summary>
    public static IQueryable<TEntity> WhereOwnedByCurrentDatabase<TEntity>(this IQueryable<TEntity> query)
        where TEntity : IMultiTenant =>
        TenantDatabaseScope.Current is { } scope ? query.WhereOwnedBy(scope) : query;
}
