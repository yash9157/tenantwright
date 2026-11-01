namespace Shared.EntityFrameworkCore.TenantDatabases;

/// <summary>
/// One physical database a service owns for a connection-string name: the shared (host) database, or a tenant's
/// dedicated database. Work against a dedicated database runs in that tenant's scope so its connection string
/// resolves and the tenant query filter applies.
/// </summary>
/// <param name="ConnectionStringName">The catalog name the database was resolved for.</param>
/// <param name="TenantId">Null for the shared database; the owning tenant for a dedicated database.</param>
/// <param name="TargetFingerprint">The <see cref="PostgreSqlDatabaseTarget.Fingerprint"/>; safe to log.</param>
public sealed record TenantDatabase(string ConnectionStringName, Guid? TenantId, string TargetFingerprint)
{
    public bool IsShared => TenantId is null;

    /// <summary>Short, log-safe database key: the first 12 fingerprint characters.</summary>
    public string LogKey => TargetFingerprint.Length > 12 ? TargetFingerprint[..12] : TargetFingerprint;
}

/// <summary>
/// The distinct databases of one connection-string name at one point in time.
/// </summary>
/// <param name="Databases">The shared database first, then each usable dedicated database in tenant-id order.</param>
/// <param name="ConflictingTenantIds">
/// Tenants whose dedicated connection string targets the shared database or the same database as another tenant.
/// They are excluded from <paramref name="Databases"/> and fail closed until corrected.
/// </param>
/// <param name="UnresolvableTenantIds">
/// Active tenants that list a dedicated database whose connection string could not be resolved or parsed. They are
/// excluded from fan-out and fail closed through resolution itself.
/// </param>
/// <param name="TenantIdsWithDedicatedAssignment">
/// Every tenant that holds a dedicated assignment for the name, usable or not. The shared database does not own
/// rows of these tenants: assigning a dedicated database never moves data, so rows such a tenant left behind in the
/// shared database are inert and cross-tenant work over the shared database must skip them.
/// </param>
/// <param name="IsComplete">
/// False when the cached tenant list was unavailable: only the shared database is known and
/// <paramref name="TenantIdsWithDedicatedAssignment"/> may be missing tenants. Work that cannot run safely on a
/// partial view must check this; the snapshot is recomputed on the next cycle.
/// </param>
public sealed record TenantDatabaseTopologySnapshot(
    string ConnectionStringName,
    IReadOnlyList<TenantDatabase> Databases,
    IReadOnlySet<Guid> ConflictingTenantIds,
    IReadOnlySet<Guid> UnresolvableTenantIds,
    IReadOnlySet<Guid> TenantIdsWithDedicatedAssignment,
    bool IsComplete)
{
    public TenantDatabase Shared => Databases[0];

    public IEnumerable<TenantDatabase> Dedicated => Databases.Where(database => !database.IsShared);

    public TenantDatabase? FindDedicated(Guid tenantId) =>
        Databases.FirstOrDefault(database => database.TenantId == tenantId);
}

/// <summary>
/// One database visited by cross-tenant work together with the topology it was taken from, so the work can tell
/// which tenants' rows the database owns.
/// </summary>
public sealed record TenantDatabaseScope(TenantDatabase Database, TenantDatabaseTopologySnapshot Topology)
{
    private static readonly AsyncLocal<TenantDatabaseScope?> Ambient = new();

    /// <summary>
    /// The database <see cref="TenantDatabaseFanOut"/> is currently visiting, flowing like the current tenant; null
    /// outside fan-out. Lets processors deep in a call chain restrict cross-tenant queries without threading the scope
    /// through every application contract.
    /// </summary>
    public static TenantDatabaseScope? Current => Ambient.Value;

    internal static IDisposable Use(TenantDatabaseScope scope)
    {
        var previous = Ambient.Value;
        Ambient.Value = scope;
        return new RestoreScope(previous);
    }

    /// <summary>
    /// True when a row of <paramref name="tenantId"/> stored in <see cref="Database"/> belongs to it: host rows and
    /// shared tenants' rows for the shared database, only the owning tenant's rows for a dedicated database.
    /// </summary>
    public bool Owns(Guid? tenantId) =>
        Database.IsShared
            ? tenantId is null || !Topology.TenantIdsWithDedicatedAssignment.Contains(tenantId.Value)
            : tenantId == Database.TenantId;

    private sealed class RestoreScope(TenantDatabaseScope? previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }
}
