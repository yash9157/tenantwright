using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.Data.ConnectionStrings;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;

namespace Shared.EntityFrameworkCore.TenantDatabases;

/// <inheritdoc cref="ITenantDatabaseTopology"/>
/// <remarks>
/// Candidates come from the cached tenant list (<see cref="ITenantStore.FindListAsync"/>), whose entries carry only
/// the names of each tenant's dedicated databases; each candidate's connection string is then resolved through the
/// normal tenant-scoped <see cref="IConnectionStringResolver"/>, so fan-out and request handling can never disagree
/// about which database a tenant uses. A dedicated assignment that targets the shared database already fails closed
/// in the resolver and is reported here as unresolvable. When the tenant list is unavailable the snapshot holds only
/// the shared database, is marked incomplete and is not cached, so the next cycle retries.
/// </remarks>
public sealed class TenantDatabaseTopology : ITenantDatabaseTopology, ISingletonDependency
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<SharedConnectionStringResolverOptions> _resolverOptions;
    private readonly ILogger<TenantDatabaseTopology> _logger;
    private readonly ConcurrentDictionary<string, CachedSnapshot> _snapshots = new(StringComparer.Ordinal);

    public TenantDatabaseTopology(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<SharedConnectionStringResolverOptions> resolverOptions,
        ILogger<TenantDatabaseTopology> logger)
    {
        _scopeFactory = scopeFactory;
        _resolverOptions = resolverOptions;
        _logger = logger;
    }

    public Task<TenantDatabaseTopologySnapshot> GetSnapshotAsync(
        string connectionStringName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionStringName);
        return GetOrComputeAsync(connectionStringName, hostOnly: false, cancellationToken);
    }

    public Task<TenantDatabaseTopologySnapshot> GetSnapshotAsync(
        Type dbContextType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dbContextType);
        var connectionStringName = ConnectionStringNameAttribute.GetConnStringName(dbContextType);
        var hostOnly = dbContextType.IsDefined(typeof(IgnoreMultiTenancyAttribute), inherit: false);
        return GetOrComputeAsync(connectionStringName, hostOnly, cancellationToken);
    }

    public void Invalidate() => _snapshots.Clear();

    private async Task<TenantDatabaseTopologySnapshot> GetOrComputeAsync(
        string connectionStringName,
        bool hostOnly,
        CancellationToken cancellationToken)
    {
        var cacheKey = $"{(hostOnly ? "host" : "tenant")}:{connectionStringName}";
        if (_snapshots.TryGetValue(cacheKey, out var cached) && cached.ExpiresAtUtc > DateTime.UtcNow)
        {
            return cached.Snapshot;
        }

        var snapshot = await ComputeAsync(connectionStringName, hostOnly, cancellationToken);
        if (snapshot.IsComplete)
        {
            var ttl = TimeSpan.FromSeconds(Math.Max(1, _resolverOptions.CurrentValue.TenantCacheSeconds));
            _snapshots[cacheKey] = new CachedSnapshot(snapshot, DateTime.UtcNow.Add(ttl));
        }

        return snapshot;
    }

    private async Task<TenantDatabaseTopologySnapshot> ComputeAsync(
        string connectionStringName,
        bool hostOnly,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var resolver = scope.ServiceProvider.GetRequiredService<IConnectionStringResolver>();
        var currentTenant = scope.ServiceProvider.GetRequiredService<ICurrentTenant>();

        string sharedConnectionString;
        using (currentTenant.Change(null))
        {
            sharedConnectionString = await resolver.ResolveAsync(connectionStringName);
        }

        var shared = new TenantDatabase(
            connectionStringName,
            TenantId: null,
            PostgreSqlDatabaseTarget.FingerprintOf(sharedConnectionString));
        var conflicting = new HashSet<Guid>();
        var unresolvable = new HashSet<Guid>();
        var assigned = new HashSet<Guid>();

        if (hostOnly || !TenantWrightDatabaseCatalog.SupportsDedicatedTenantDatabase(connectionStringName))
        {
            return new TenantDatabaseTopologySnapshot(
                connectionStringName, [shared], conflicting, unresolvable, assigned, IsComplete: true);
        }

        var tenantStore = scope.ServiceProvider.GetRequiredService<ITenantStore>();
        var tenants = await tenantStore.FindListAsync(cancellationToken);
        if (tenants == null)
        {
            TenantDatabaseMetrics.RecordTopologyIncomplete(connectionStringName);
            _logger.LogWarning(
                "Tenant list is unavailable; cross-tenant work for {ConnectionName} covers only the shared database this cycle.",
                connectionStringName);
            return new TenantDatabaseTopologySnapshot(
                connectionStringName, [shared], conflicting, unresolvable, assigned, IsComplete: false);
        }

        var candidates = tenants
            .Where(tenant => tenant.IsActive &&
                             tenant.Id != Guid.Empty &&
                             tenant.DedicatedDatabaseNames.Contains(connectionStringName, StringComparer.Ordinal))
            .Select(tenant => tenant.Id)
            .Distinct()
            .Order()
            .ToArray();

        var dedicatedByFingerprint = new Dictionary<string, List<Guid>>(StringComparer.Ordinal);
        foreach (var tenantId in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string tenantConnectionString;
            try
            {
                using (currentTenant.Change(tenantId))
                {
                    tenantConnectionString = await resolver.ResolveAsync(connectionStringName);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                assigned.Add(tenantId);
                unresolvable.Add(tenantId);
                TenantDatabaseMetrics.RecordTopologyExclusion(connectionStringName, "unresolvable");
                _logger.LogWarning(
                    "Dedicated tenant database could not be resolved for {ConnectionName} and was excluded from fan-out ({ErrorType}). TenantHash={TenantHash}.",
                    connectionStringName,
                    exception.GetType().Name,
                    TenantResolutionHash.HashTenantId(tenantId));
                continue;
            }

            if (string.Equals(tenantConnectionString, sharedConnectionString, StringComparison.Ordinal))
            {
                // The resolver fell back to the host database: the listed dedicated value is not visible yet (a
                // list entry ahead of the tenant entry). The tenant is served by the shared database like any other.
                continue;
            }

            assigned.Add(tenantId);
            if (!PostgreSqlDatabaseTarget.TryParse(tenantConnectionString, out var target))
            {
                unresolvable.Add(tenantId);
                TenantDatabaseMetrics.RecordTopologyExclusion(connectionStringName, "unparsable");
                _logger.LogWarning(
                    "Dedicated tenant database for {ConnectionName} is not a valid PostgreSQL target and was excluded from fan-out. TenantHash={TenantHash}.",
                    connectionStringName,
                    TenantResolutionHash.HashTenantId(tenantId));
                continue;
            }

            if (!dedicatedByFingerprint.TryGetValue(target.Fingerprint, out var owners))
            {
                owners = [];
                dedicatedByFingerprint[target.Fingerprint] = owners;
            }

            owners.Add(tenantId);
        }

        var databases = new List<TenantDatabase> { shared };
        foreach (var (fingerprint, owners) in dedicatedByFingerprint.OrderBy(pair => pair.Value[0]))
        {
            if (owners.Count > 1)
            {
                conflicting.UnionWith(owners);
                TenantDatabaseMetrics.RecordTopologyExclusion(connectionStringName, "shared_by_tenants");
                _logger.LogError(
                    "{TenantCount} tenants target the same dedicated database for {ConnectionName}; all of them are excluded from fan-out until it is corrected. DatabaseKey={DatabaseKey}.",
                    owners.Count,
                    connectionStringName,
                    fingerprint[..12]);
                continue;
            }

            databases.Add(new TenantDatabase(connectionStringName, owners[0], fingerprint));
        }

        return new TenantDatabaseTopologySnapshot(
            connectionStringName, databases, conflicting, unresolvable, assigned, IsComplete: true);
    }

    private sealed record CachedSnapshot(TenantDatabaseTopologySnapshot Snapshot, DateTime ExpiresAtUtc);
}
