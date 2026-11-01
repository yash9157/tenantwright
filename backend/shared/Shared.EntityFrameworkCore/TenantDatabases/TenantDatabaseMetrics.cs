using System.Diagnostics.Metrics;

namespace Shared.EntityFrameworkCore.TenantDatabases;

/// <summary>
/// Operator signals for dedicated tenant databases. Tags carry only catalog names, outcome tokens and short target
/// fingerprints; never connection strings, hosts, database names or tenant identifiers.
/// </summary>
public static class TenantDatabaseMetrics
{
    private static readonly Meter Meter = new("Shared.EntityFrameworkCore.TenantDatabases");

    private static readonly Counter<long> TopologyExclusionCounter =
        Meter.CreateCounter<long>("shared.tenant_databases.topology_exclusions_total");

    private static readonly Counter<long> TopologyIncompleteCounter =
        Meter.CreateCounter<long>("shared.tenant_databases.topology_incomplete_total");

    private static readonly Counter<long> FanOutFailureCounter =
        Meter.CreateCounter<long>("shared.tenant_databases.fan_out_failures_total");

    private static readonly Counter<long> MigrationCounter =
        Meter.CreateCounter<long>("shared.tenant_databases.migrations_total");

    private static readonly Counter<long> NotMigratedCounter =
        Meter.CreateCounter<long>("shared.tenant_databases.not_migrated_total");

    /// <summary>A tenant's dedicated database was left out of fan-out (conflicting or unusable target).</summary>
    public static void RecordTopologyExclusion(string connectionStringName, string reason) =>
        TopologyExclusionCounter.Add(
            1,
            new KeyValuePair<string, object?>("database", connectionStringName),
            new KeyValuePair<string, object?>("reason", reason));

    /// <summary>The cached tenant list was unavailable, so only the shared database could be enumerated.</summary>
    public static void RecordTopologyIncomplete(string connectionStringName) =>
        TopologyIncompleteCounter.Add(1, new KeyValuePair<string, object?>("database", connectionStringName));

    /// <summary>Cross-tenant work failed for one database; the remaining databases were still processed.</summary>
    public static void RecordFanOutFailure(string connectionStringName, string work, string errorType) =>
        FanOutFailureCounter.Add(
            1,
            new KeyValuePair<string, object?>("database", connectionStringName),
            new KeyValuePair<string, object?>("work", work),
            new KeyValuePair<string, object?>("error_type", errorType));

    /// <summary>A dedicated tenant database migration attempt finished with <paramref name="outcome"/>.</summary>
    public static void RecordMigration(string connectionStringName, string trigger, string outcome) =>
        MigrationCounter.Add(
            1,
            new KeyValuePair<string, object?>("database", connectionStringName),
            new KeyValuePair<string, object?>("trigger", trigger),
            new KeyValuePair<string, object?>("outcome", outcome));

    /// <summary>A request reached a dedicated tenant database whose schema is behind the compiled model.</summary>
    public static void RecordNotMigrated(string connectionStringName) =>
        NotMigratedCounter.Add(1, new KeyValuePair<string, object?>("database", connectionStringName));
}
