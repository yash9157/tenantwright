using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Permissions.Domain.Grants;
using Shared.Data.Filtering;
using Shared.Domain;
using Shared.Domain.Repositories;
using Shared.EntityFrameworkCore.TenantDatabases;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;

namespace Permissions.Application.Caching;

public class PermissionProviderCacheRepairService : ITransientDependency
{
    private readonly IRepository<PermissionGrant, Guid> _grantRepository;
    private readonly PermissionProviderGrantCacheSyncService _sync;
    private readonly PermissionStampService _stampService;
    private readonly IDataFilter _dataFilter;
    private readonly ICurrentTenant _currentTenant;
    private readonly ILogger<PermissionProviderCacheRepairService> _logger;

    public PermissionProviderCacheRepairService(
        IRepository<PermissionGrant, Guid> grantRepository,
        PermissionProviderGrantCacheSyncService sync,
        PermissionStampService stampService,
        IDataFilter dataFilter,
        ICurrentTenant currentTenant,
        ILogger<PermissionProviderCacheRepairService> logger)
    {
        _grantRepository = grantRepository;
        _sync = sync;
        _stampService = stampService;
        _dataFilter = dataFilter;
        _currentTenant = currentTenant;
        _logger = logger;
    }

    public async Task<long> EnsureProviderCacheAsync(
        Guid? tenantId,
        string providerName,
        string providerKey,
        CancellationToken cancellationToken = default)
    {
        providerName = providerName.Trim();
        providerKey = providerKey.Trim();

        var stamp = await _stampService.GetAsync(tenantId, providerName, providerKey, cancellationToken: cancellationToken);
        if (stamp > 0)
        {
            return stamp;
        }

        if (!await HasProviderGrantsAsync(tenantId, providerName, providerKey, cancellationToken))
        {
            return 0;
        }

        stamp = await _stampService.GetNextStampAsync(tenantId, providerName, providerKey, cancellationToken: cancellationToken);
        await _sync.SyncAsync(tenantId, providerName, providerKey, stamp, considerUow: false, cancellationToken: cancellationToken);
        await _stampService.BumpAsync(tenantId, providerName, providerKey, stamp, considerUow: false, cancellationToken: cancellationToken);

        _logger.LogWarning(
            "Repaired missing permission provider cache for tenant {TenantId}, provider {ProviderName}:{ProviderKey}, stamp {Stamp}.",
            tenantId,
            providerName,
            providerKey,
            stamp);

        return stamp;
    }

    public async Task RefreshAllProvidersAsync(CancellationToken cancellationToken = default)
    {
        var providers = await GetGrantProvidersAsync(cancellationToken);

        foreach (var provider in providers)
        {
            // Each provider is rebuilt inside its own tenant scope so the tenant query filter selects
            // that tenant's grants; the ambient host scope of this job would otherwise read none of them.
            using (_currentTenant.Change(provider.TenantId))
            {
                await RefreshProviderAsync(provider, cancellationToken);
            }
        }

        _logger.LogInformation("Refreshed permission provider cache for {ProviderCount} providers.", providers.Count);
    }

    private async Task RefreshProviderAsync(PermissionGrantProvider provider, CancellationToken cancellationToken)
    {
        var stamp = await _stampService.GetAsync(
            provider.TenantId,
            provider.ProviderName,
            provider.ProviderKey,
            cancellationToken: cancellationToken);

        if (stamp <= 0)
        {
            stamp = await _stampService.GetNextStampAsync(provider.TenantId, provider.ProviderName, provider.ProviderKey, cancellationToken: cancellationToken);
            await _sync.SyncAsync(provider.TenantId, provider.ProviderName, provider.ProviderKey, stamp, considerUow: false, cancellationToken: cancellationToken);
            await _stampService.BumpAsync(provider.TenantId, provider.ProviderName, provider.ProviderKey, stamp, considerUow: false, cancellationToken: cancellationToken);
            return;
        }

        await _sync.SyncAsync(provider.TenantId, provider.ProviderName, provider.ProviderKey, stamp, considerUow: false, cancellationToken: cancellationToken);
        await _stampService.SetAsync(provider.TenantId, provider.ProviderName, provider.ProviderKey, stamp, considerUow: false, cancellationToken: cancellationToken);
    }

    private async Task<bool> HasProviderGrantsAsync(
        Guid? tenantId,
        string providerName,
        string providerKey,
        CancellationToken cancellationToken)
    {
        var queryable = await _grantRepository.GetQueryableAsync();
        return await queryable.AnyAsync(
            x => x.TenantId == tenantId &&
                 x.ProviderName == providerName &&
                 x.ProviderKey == providerKey,
            cancellationToken: cancellationToken);
    }

    private async Task<List<PermissionGrantProvider>> GetGrantProvidersAsync(CancellationToken cancellationToken)
    {
        // Cache maintenance is a host job that must see every tenant's providers. The filter is lifted
        // only for this read-only enumeration; grants are loaded per tenant in RefreshProviderAsync.
        // Under database fan-out the enumeration covers only the providers the visited database owns, so a
        // tenant with a dedicated database is rebuilt from that database and never from rows it left behind
        // in the shared one.
        using (_dataFilter.Disable<IMultiTenant>())
        {
            var queryable = await _grantRepository.GetQueryableAsync();
            var providerRows = await queryable
                .WhereOwnedByCurrentDatabase()
                .Select(x => new { x.TenantId, x.ProviderName, x.ProviderKey })
                .Distinct()
                .ToListAsync(cancellationToken);

            return providerRows
                .Select(x => new PermissionGrantProvider(x.TenantId, x.ProviderName, x.ProviderKey))
                .ToList();
        }
    }

    private sealed record PermissionGrantProvider(Guid? TenantId, string ProviderName, string ProviderKey);
}
