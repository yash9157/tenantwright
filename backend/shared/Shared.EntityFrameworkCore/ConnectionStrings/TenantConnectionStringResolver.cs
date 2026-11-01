using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Shared.Data.ConnectionStrings;
using Shared.EntityFrameworkCore.TenantDatabases;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;

namespace Shared.EntityFrameworkCore.ConnectionStrings;

/// <summary>
/// Resolves the connection string for a service database name.
/// <para>
/// Host scope: the configured <c>ConnectionStrings:{name}</c>, then a database mapping, then
/// <c>ConnectionStrings:Default</c>.
/// </para>
/// <para>
/// Tenant scope: an unknown tenant or a positively-known inactive tenant fails closed for every name. For a name
/// in <see cref="TenantWrightDatabaseCatalog.TenantDedicatable"/> the order is the tenant's own connection string for
/// that exact name (failing closed if it targets the shared database), then the host database when
/// <see cref="SharedConnectionStringResolverOptions.AllowHostFallbackForTenant"/> is set, then fail closed. Any
/// other name is host-only by catalog: the tenant's stored values are never consulted and the host database is
/// returned. There is no tenant-level "Default" fallback.
/// </para>
/// <para>
/// An unknown tenant fails closed even when host fallback is allowed. A cold or flushed tenant cache is
/// indistinguishable from an unknown tenant, but serving an unverified tenant id from the shared database would let
/// a forged or deleted tenant reach data. Availability is preserved elsewhere: HTTP requests for an unknown tenant
/// are already rejected by the multi-tenancy middleware before resolution, misses are never pinned in-process
/// (<see cref="TenantConnectionStringsCache"/>), the tenant store negative-caches for seconds only, and the Tenant
/// service's warmup and reconcile worker repopulate the cache; message consumers fail and are retried.
/// </para>
/// </summary>
[Dependency(ServiceLifetime.Transient, ReplaceServices = true)]
public class TenantConnectionStringResolver : IConnectionStringResolver, ITransientDependency
{
    private readonly ICurrentTenant _currentTenant;
    private readonly ITenantStore _tenantStore;
    private readonly ITenantConnectionStringsCache _tenantConnectionStringsCache;
    private readonly ITenantConnectionStringProtector _tenantConnectionStringProtector;
    private readonly IOptionsMonitor<SharedDbConnectionOptions> _options;
    private readonly IOptionsMonitor<SharedConnectionStringResolverOptions> _resolverOptions;
    private readonly ILogger<TenantConnectionStringResolver> _logger;

    public TenantConnectionStringResolver(
        ICurrentTenant currentTenant,
        ITenantStore tenantStore,
        ITenantConnectionStringsCache tenantConnectionStringsCache,
        ITenantConnectionStringProtector tenantConnectionStringProtector,
        IOptionsMonitor<SharedDbConnectionOptions> options,
        IOptionsMonitor<SharedConnectionStringResolverOptions> resolverOptions,
        ILogger<TenantConnectionStringResolver> logger)
    {
        _currentTenant = currentTenant;
        _tenantStore = tenantStore;
        _tenantConnectionStringsCache = tenantConnectionStringsCache;
        _tenantConnectionStringProtector = tenantConnectionStringProtector;
        _options = options;
        _resolverOptions = resolverOptions;
        _logger = logger;
    }

    public async Task<string> ResolveAsync(string? connectionStringName = null)
    {
        if (!HasTenantContext())
        {
            return EnsureResolved(ResolveHostConnectionString(connectionStringName), connectionStringName);
        }

        var tenantId = _currentTenant.Id!.Value;
        var tenant = await GetCachedTenantAsync(tenantId);

        if (!tenant.Exists)
        {
            // A cold or flushed Valkey cache and a genuinely unknown tenant look identical here; both fail closed
            // (see the type remarks). Recurring warnings for a tenant that should exist, together with the "miss"
            // result on shared.tenant.store.cache.operations, are the signal that the cache lost data.
            _logger.LogWarning(
                "Tenant not found in cache during connection-string resolution in {Resolver}. TenantHash={TenantHash}.",
                nameof(TenantConnectionStringResolver),
                TenantResolutionHash.HashTenantId(tenantId));

            return FailClosed(connectionStringName);
        }

        // Deactivation hard-stop: a positively-known inactive tenant never reaches any database, including the
        // host fallback and host-only names. This overrides AllowHostFallbackForTenant.
        if (!tenant.IsActive)
        {
            ConnectionStringResolutionMetrics.RecordTenantFailClosed(connectionStringName);
            _logger.LogWarning(
                "Blocked connection-string resolution for a deactivated tenant in {Resolver}. ConnectionNameHash={ConnectionNameHash}, TenantHash={TenantHash}.",
                nameof(TenantConnectionStringResolver),
                HashName(connectionStringName),
                TenantResolutionHash.HashTenantId(tenantId));

            throw new InvalidOperationException(
                $"Tenant '{tenantId:D}' is not active; connection-string resolution is blocked.");
        }

        if (!TenantWrightDatabaseCatalog.SupportsDedicatedTenantDatabase(connectionStringName))
        {
            // Host-only by catalog: tenant-stored values are never consulted for this name.
            return EnsureResolved(ResolveHostConnectionString(connectionStringName), connectionStringName);
        }

        if (tenant.ConnectionStrings.TryGetValue(connectionStringName!, out var dedicated) &&
            !string.IsNullOrWhiteSpace(dedicated))
        {
            if (tenant.NamesTargetingSharedDatabase.Contains(connectionStringName!))
            {
                _logger.LogError(
                    "Dedicated tenant database targets the shared database for {ConnectionName}; resolution fails closed until the assignment is corrected. TenantHash={TenantHash}.",
                    connectionStringName,
                    TenantResolutionHash.HashTenantId(tenantId));
                return FailClosed(connectionStringName);
            }

            return dedicated;
        }

        if (_resolverOptions.CurrentValue.AllowHostFallbackForTenant)
        {
            var hostFallback = ResolveHostConnectionString(connectionStringName);
            if (!string.IsNullOrWhiteSpace(hostFallback))
            {
                // Shared-database tenancy is the default topology, so this path runs on every shared-tenant
                // request. It is counted for operators rather than logged as a warning.
                ConnectionStringResolutionMetrics.RecordTenantHostFallback(connectionStringName);
                return hostFallback;
            }
        }

        return FailClosed(connectionStringName);
    }

    private string? ResolveHostConnectionString(string? connectionStringName)
    {
        var options = _options.CurrentValue;

        if (connectionStringName == null)
        {
            return options.ConnectionStrings.Default;
        }

        return options.GetConnectionStringOrNull(connectionStringName, fallbackToDatabaseMappings: true, fallbackToDefault: true);
    }

    private async Task<TenantConnectionStringsCacheItem> GetCachedTenantAsync(Guid tenantId)
    {
        return await _tenantConnectionStringsCache.GetOrAddAsync(
            tenantId,
            async () =>
            {
                var found = await _tenantStore.FindAsync(tenantId);
                if (found == null)
                {
                    return new TenantConnectionStringsCacheItem { Exists = false };
                }

                // Only dedicatable names are ever decrypted; a stored value under any other name is inert.
                var connectionStrings = found.ConnectionStrings
                    .Where(pair => TenantWrightDatabaseCatalog.SupportsDedicatedTenantDatabase(pair.Key))
                    .ToDictionary(
                        pair => pair.Key,
                        pair => _tenantConnectionStringProtector.Unprotect(tenantId, pair.Key, pair.Value),
                        StringComparer.Ordinal);

                return new TenantConnectionStringsCacheItem
                {
                    Exists = true,
                    IsActive = found.IsActive,
                    ConnectionStrings = connectionStrings,
                    NamesTargetingSharedDatabase = connectionStrings
                        .Where(pair => TargetsSharedDatabase(pair.Key, pair.Value))
                        .Select(pair => pair.Key)
                        .ToHashSet(StringComparer.Ordinal)
                };
            });
    }

    private bool TargetsSharedDatabase(string connectionStringName, string dedicatedConnectionString)
    {
        var shared = ResolveHostConnectionString(connectionStringName);
        return !string.IsNullOrWhiteSpace(shared) &&
               string.Equals(
                   PostgreSqlDatabaseTarget.FingerprintOf(dedicatedConnectionString),
                   PostgreSqlDatabaseTarget.FingerprintOf(shared),
                   StringComparison.Ordinal);
    }

    private string EnsureResolved(string? connectionString, string? connectionStringName)
    {
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            return connectionString;
        }

        if (!_resolverOptions.CurrentValue.ThrowOnMissing)
        {
            return string.Empty;
        }

        ConnectionStringResolutionMetrics.RecordFailure(nameof(TenantConnectionStringResolver), connectionStringName);
        _logger.LogError(
            "Connection string resolution failed in {Resolver}. ConnectionNameHash={ConnectionNameHash}, TenantHash={TenantHash}.",
            nameof(TenantConnectionStringResolver),
            HashName(connectionStringName),
            TenantResolutionHash.HashTenantId(_currentTenant.Id));

        throw new InvalidOperationException(
            $"Connection string '{connectionStringName ?? Shared.Data.ConnectionStrings.ConnectionStrings.DefaultConnectionStringName}' could not be resolved.");
    }

    private string FailClosed(string? connectionStringName)
    {
        ConnectionStringResolutionMetrics.RecordFailure(nameof(TenantConnectionStringResolver), connectionStringName);
        ConnectionStringResolutionMetrics.RecordTenantFailClosed(connectionStringName);

        _logger.LogError(
            "Tenant-scoped connection string resolution failed closed in {Resolver}. ConnectionNameHash={ConnectionNameHash}, TenantHash={TenantHash}.",
            nameof(TenantConnectionStringResolver),
            HashName(connectionStringName),
            TenantResolutionHash.HashTenantId(_currentTenant.Id));

        throw new InvalidOperationException(
            $"Tenant-scoped connection string '{connectionStringName ?? Shared.Data.ConnectionStrings.ConnectionStrings.DefaultConnectionStringName}' could not be resolved.");
    }

    private static string HashName(string? connectionStringName) =>
        TenantResolutionHash.HashString(
            connectionStringName ?? Shared.Data.ConnectionStrings.ConnectionStrings.DefaultConnectionStringName);

    private bool HasTenantContext()
    {
        return _currentTenant.Id.HasValue && _currentTenant.Id.Value != Guid.Empty;
    }
}
