using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using Shared.Caching.Abstractions;
using Shared.Domain;
using Shared.Domain.Repositories;
using Shared.Data.Filtering;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy.Caching;
using Microsoft.Extensions.Logging;
using Shared.Data.ConnectionStrings;
using Shared.DistributedLocking.Abstractions;
using Shared.DistributedLocking.Options;
using Shared.DistributedLocking.Policies;
using Tenant.Domain.Tenants;
using TenantEntity = Tenant.Domain.Tenants.Tenant;
using Microsoft.EntityFrameworkCore;

namespace Tenant.Application.Tenants;

internal sealed record TenantSelectionCacheItem(Guid Id, string Name, bool IsActive);

public class TenantCacheManager : ITransientDependency
{
    private static readonly TimeSpan WarmupRefreshTimeout = TimeSpan.FromSeconds(15);
    private const string TenantListMutationLockName = "tenant:list-cache:mutation";
    private static readonly DistributedLockAcquireOptions TenantListMutationLockOptions = new()
    {
        PolicyName = DistributedLockPolicyNames.TenantListCacheMutation,
        IgnoreTenant = true
    };

    private readonly IRepository<TenantEntity, Guid> _tenantRepository;
    private readonly IDistributedCache<TenantCacheItem, string> _tenantByIdCache;
    private readonly IDistributedCache<TenantByNameCacheItem, string> _tenantByNameCache;
    private readonly IDistributedCache<TenantListCacheItem, string> _tenantListCache;
    private readonly IDistributedCache<TenantListStampCacheItem, string> _tenantListStampCache;
    private readonly ITenantConnectionStringProtector _connectionStringProtector;
    private readonly IDistributedLock _distributedLock;
    private readonly IDataFilter _dataFilter;
    private readonly ILogger<TenantCacheManager> _logger;
    private readonly TimeSpan _cacheTtl;
    private readonly TimeSpan _stampCacheTtl;

    public TenantCacheManager(
        IRepository<TenantEntity, Guid> tenantRepository,
        IDistributedCache<TenantCacheItem, string> tenantByIdCache,
        IDistributedCache<TenantByNameCacheItem, string> tenantByNameCache,
        IDistributedCache<TenantListCacheItem, string> tenantListCache,
        IDistributedCache<TenantListStampCacheItem, string> tenantListStampCache,
        ITenantConnectionStringProtector connectionStringProtector,
        IDistributedLock distributedLock,
        IDataFilter dataFilter,
        IOptions<TenantCacheOptions> tenantCacheOptions,
        ILogger<TenantCacheManager> logger)
    {
        _tenantRepository = tenantRepository;
        _tenantByIdCache = tenantByIdCache;
        _tenantByNameCache = tenantByNameCache;
        _tenantListCache = tenantListCache;
        _tenantListStampCache = tenantListStampCache;
        _connectionStringProtector = connectionStringProtector;
        _distributedLock = distributedLock;
        _dataFilter = dataFilter;
        var maxAbsoluteExpirationHours = tenantCacheOptions.Value.MaxAbsoluteExpirationHours;
        _cacheTtl = TimeSpan.FromHours(maxAbsoluteExpirationHours);
        _stampCacheTtl = _cacheTtl;
        _logger = logger;
    }

    public async Task WarmupAsync(CancellationToken cancellationToken = default)
    {
        var stamp = await _tenantListStampCache.GetOrAddAsync(
            TenantCacheConstants.TenantListStampKey,
            () => Task.FromResult(new TenantListStampCacheItem { Stamp = 1 }),
            optionsFactory: CreateStampCacheOptions,
            considerUow: false,
            token: cancellationToken);

        var list = await _tenantListCache.GetAsync(TenantCacheConstants.TenantListKey, considerUow: false, token: cancellationToken);
        if (list != null && list.Tenants.Count > 0 && stamp != null && list.Stamp == stamp.Stamp)
        {
            return;
        }

        using var refreshTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        refreshTimeout.CancelAfter(WarmupRefreshTimeout);
        try
        {
            await RefreshAllAsync(refreshTimeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Tenant cache warmup full refresh exceeded timeout ({WarmupTimeoutSeconds}s). Startup continues with existing cache state.",
                WarmupRefreshTimeout.TotalSeconds);
        }
    }

    internal async Task<IReadOnlyList<TenantSelectionCacheItem>> GetSelectionOptionsAsync(
        CancellationToken cancellationToken = default)
    {
        await WarmupAsync(cancellationToken);

        var stamp = await _tenantListStampCache.GetAsync(
            TenantCacheConstants.TenantListStampKey,
            considerUow: false,
            token: cancellationToken);
        var list = await _tenantListCache.GetAsync(
            TenantCacheConstants.TenantListKey,
            considerUow: false,
            token: cancellationToken);

        if (list == null || stamp == null || list.Stamp != stamp.Stamp)
        {
            return [];
        }

        return list.Tenants
            .Where(item => item.Exists)
            .Select(item => new TenantSelectionCacheItem(item.Id, item.Name, item.IsActive))
            .ToList();
    }

    public async Task UpsertTenantAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var query = (await _tenantRepository.WithDetailsAsync(x => x.ConnectionStrings)).AsNoTracking();
        var tenant = await query.FirstOrDefaultAsync(x => x.Id == tenantId, cancellationToken);

        if (tenant == null)
        {
            await RemoveTenantAsync(tenantId, cancellationToken: cancellationToken);
            return;
        }

        await UpsertTenantAsync(tenant, cancellationToken);
    }

    public async Task UpsertTenantAsync(TenantEntity tenant, CancellationToken cancellationToken = default)
    {
        var tenantItem = MapToCacheItem(tenant);
        var options = CreateCacheOptions();

        await _tenantByIdCache.SetAsync(
            tenant.Id.ToString("D"),
            tenantItem,
            options: options,
            considerUow: false,
            token: cancellationToken);

        var normalizedName = NormalizeName(tenant.NormalizedName, tenant.Name);
        await _tenantByNameCache.SetAsync(
            normalizedName,
            new TenantByNameCacheItem
            {
                Exists = true,
                TenantId = tenant.Id,
                Tenant = tenantItem
            },
            options: options,
            considerUow: false,
            token: cancellationToken);

        await using var mutationLock = await AcquireTenantListMutationLockAsync(cancellationToken);
        var stamp = await BumpTenantListStampAsync(cancellationToken);
        var cachedList = await _tenantListCache.GetAsync(
            TenantCacheConstants.TenantListKey,
            considerUow: false,
            token: cancellationToken);
        if (cachedList == null)
        {
            return;
        }

        var existingIndex = cachedList.Tenants.FindIndex(item => item.Id == tenant.Id);
        if (existingIndex >= 0)
        {
            cachedList.Tenants[existingIndex] = ToListEntry(tenantItem);
        }
        else
        {
            cachedList.Tenants.Add(ToListEntry(tenantItem));
        }

        cachedList.Stamp = stamp;
        await _tenantListCache.SetAsync(
            TenantCacheConstants.TenantListKey,
            cachedList,
            options: options,
            considerUow: false,
            token: cancellationToken);
    }

    /// <summary>
    /// Marks a tenant as positively-known-inactive in the cache instead of
    /// removing it: the by-id entry is kept with <c>Exists = true, IsActive =
    /// false</c> and no connection strings, while the by-name entry is removed
    /// (so login-by-name can't resolve it) and the tenant is dropped from the
    /// cached list. This is what lets
    /// <c>TenantConnectionStringResolver</c>'s fail-closed check
    /// (<c>Exists &amp;&amp; !IsActive</c>) fire for a soft-deleted tenant even on
    /// services that allow host fallback — a bare removal would instead be
    /// indistinguishable from a cold cache and fall back open.
    /// <see cref="RefreshAllAsync"/> also re-asserts a tombstone for every
    /// soft-deleted tenant on each reconcile pass, so this state does not expire
    /// with the cache TTL between deletion and restore; it is cleared by
    /// <see cref="UpsertTenantAsync(Guid,CancellationToken)"/> on restore.
    /// </summary>
    public async Task TombstoneTenantAsync(Guid tenantId, string? oldNormalizedName = null, CancellationToken cancellationToken = default)
    {
        var options = CreateCacheOptions();
        await _tenantByIdCache.SetAsync(
            tenantId.ToString("D"),
            new TenantCacheItem
            {
                Exists = true,
                Id = tenantId,
                IsActive = false,
                ConnectionStrings = new Dictionary<string, string>(StringComparer.Ordinal)
            },
            options: options,
            considerUow: false,
            token: cancellationToken);

        if (!string.IsNullOrWhiteSpace(oldNormalizedName))
        {
            await _tenantByNameCache.RemoveAsync(NormalizeName(oldNormalizedName, oldNormalizedName), considerUow: false, token: cancellationToken);
        }

        await using var mutationLock = await AcquireTenantListMutationLockAsync(cancellationToken);
        var stamp = await BumpTenantListStampAsync(cancellationToken);
        var cachedList = await _tenantListCache.GetAsync(
            TenantCacheConstants.TenantListKey,
            considerUow: false,
            token: cancellationToken);
        if (cachedList == null)
        {
            return;
        }

        cachedList.Tenants.RemoveAll(item => item.Id == tenantId);
        cachedList.Stamp = stamp;
        await _tenantListCache.SetAsync(
            TenantCacheConstants.TenantListKey,
            cachedList,
            options: CreateCacheOptions(),
            considerUow: false,
            token: cancellationToken);
    }

    public async Task RemoveTenantAsync(Guid tenantId, string? oldNormalizedName = null, CancellationToken cancellationToken = default)
    {
        await _tenantByIdCache.RemoveAsync(tenantId.ToString("D"), considerUow: false, token: cancellationToken);

        if (!string.IsNullOrWhiteSpace(oldNormalizedName))
        {
            await _tenantByNameCache.RemoveAsync(NormalizeName(oldNormalizedName, oldNormalizedName), considerUow: false, token: cancellationToken);
        }

        await using var mutationLock = await AcquireTenantListMutationLockAsync(cancellationToken);
        var stamp = await BumpTenantListStampAsync(cancellationToken);
        var cachedList = await _tenantListCache.GetAsync(
            TenantCacheConstants.TenantListKey,
            considerUow: false,
            token: cancellationToken);
        if (cachedList == null)
        {
            return;
        }

        cachedList.Tenants.RemoveAll(item => item.Id == tenantId);
        cachedList.Stamp = stamp;
        await _tenantListCache.SetAsync(
            TenantCacheConstants.TenantListKey,
            cachedList,
            options: CreateCacheOptions(),
            considerUow: false,
            token: cancellationToken);
    }

    public async Task<long> BumpTenantListStampAsync(CancellationToken cancellationToken = default)
    {
        var stampItem = await _tenantListStampCache.GetOrAddAsync(
            TenantCacheConstants.TenantListStampKey,
            () => Task.FromResult(new TenantListStampCacheItem { Stamp = 0 }),
            optionsFactory: CreateStampCacheOptions,
            considerUow: false,
            token: cancellationToken)
            ?? new TenantListStampCacheItem { Stamp = 0 };

        stampItem.Stamp++;
        await _tenantListStampCache.SetAsync(
            TenantCacheConstants.TenantListStampKey,
            stampItem,
            options: CreateStampCacheOptions(),
            considerUow: false,
            token: cancellationToken);

        return stampItem.Stamp;
    }

    public async Task RefreshAllAsync(CancellationToken cancellationToken = default)
    {
        await using var mutationLock = await AcquireTenantListMutationLockAsync(cancellationToken);
        await RefreshAllUnderLockAsync(cancellationToken);
    }

    private async Task RefreshAllUnderLockAsync(CancellationToken cancellationToken)
    {
        var query = (await _tenantRepository.WithDetailsAsync(x => x.ConnectionStrings)).AsNoTracking();
        var tenants = await query.ToListAsync(cancellationToken);

        var options = CreateCacheOptions();
        var stamp = await BumpTenantListStampAsync(cancellationToken);

        var byIdItems = new List<KeyValuePair<string, TenantCacheItem>>(tenants.Count);
        var byNameItems = new List<KeyValuePair<string, TenantByNameCacheItem>>(tenants.Count);
        var listTenants = new List<TenantCacheItem>(tenants.Count);

        foreach (var tenant in tenants)
        {
            var tenantItem = MapToCacheItem(tenant);
            byIdItems.Add(new KeyValuePair<string, TenantCacheItem>(tenant.Id.ToString("D"), tenantItem));

            var normalizedName = NormalizeName(tenant.NormalizedName, tenant.Name);
            byNameItems.Add(new KeyValuePair<string, TenantByNameCacheItem>(
                normalizedName,
                new TenantByNameCacheItem
                {
                    Exists = true,
                    TenantId = tenant.Id,
                    Tenant = tenantItem
                }));

            listTenants.Add(ToListEntry(tenantItem));
        }

        // Also re-assert a tombstone (Exists=true, IsActive=false) for every
        // soft-deleted tenant, alongside the live tenants above. Without this,
        // a soft-delete tombstone would only exist until its cache TTL expires
        // (TombstoneTenantAsync is called once, at delete time), after which a
        // reconcile pass would leave it as a cold-miss and re-open the fail-open
        // gap on services that allow host fallback. By-name and the cached list
        // stay live-only: a deleted tenant must still not resolve by name or
        // appear in listings.
        List<TenantEntity> deletedTenants;
        using (_dataFilter.Disable<ISoftDelete>())
        {
            var deletedQuery = (await _tenantRepository.GetQueryableAsync()).AsNoTracking();
            deletedTenants = await deletedQuery.Where(x => x.IsDeleted).ToListAsync(cancellationToken);
        }

        foreach (var deletedTenant in deletedTenants)
        {
            byIdItems.Add(new KeyValuePair<string, TenantCacheItem>(
                deletedTenant.Id.ToString("D"),
                new TenantCacheItem
                {
                    Exists = true,
                    Id = deletedTenant.Id,
                    IsActive = false,
                    ConnectionStrings = new Dictionary<string, string>(StringComparer.Ordinal)
                }));
        }

        await _tenantByIdCache.SetManyAsync(byIdItems, options: options, considerUow: false, token: cancellationToken);
        await _tenantByNameCache.SetManyAsync(byNameItems, options: options, considerUow: false, token: cancellationToken);

        var listItem = new TenantListCacheItem
        {
            Stamp = stamp,
            Tenants = listTenants
        };

        await _tenantListCache.SetAsync(TenantCacheConstants.TenantListKey, listItem, options: options, considerUow: false, token: cancellationToken);
    }

    private Task<IDistributedLockHandle> AcquireTenantListMutationLockAsync(CancellationToken cancellationToken)
    {
        return _distributedLock.AcquireAsync(
            TenantListMutationLockName,
            TenantListMutationLockOptions,
            cancellationToken);
    }

    private TenantCacheItem MapToCacheItem(TenantEntity tenant)
    {
        return new TenantCacheItem
        {
            Exists = true,
            Id = tenant.Id,
            Name = tenant.Name,
            NormalizedName = NormalizeName(tenant.NormalizedName, tenant.Name),
            IsActive = tenant.IsActive,
            ConnectionStrings = tenant.ConnectionStrings
                .Where(x => TenantWrightDatabaseCatalog.SupportsDedicatedTenantDatabase(x.Name) &&
                            !string.IsNullOrWhiteSpace(x.GetStoredValue()))
                .ToDictionary(
                    x => x.Name,
                    x => ProtectForCache(tenant.Id, x.Name, x.GetStoredValue()!),
                    StringComparer.Ordinal)
        };
    }

    /// <summary>
    /// List entries never carry protected connection-string values, only the names of the tenant's dedicated
    /// databases, so services can enumerate dedicated databases without a per-tenant lookup.
    /// </summary>
    private static TenantCacheItem ToListEntry(TenantCacheItem tenantItem)
    {
        return new TenantCacheItem
        {
            Exists = true,
            Id = tenantItem.Id,
            Name = tenantItem.Name,
            NormalizedName = tenantItem.NormalizedName,
            IsActive = tenantItem.IsActive,
            ConnectionStrings = new Dictionary<string, string>(StringComparer.Ordinal),
            DedicatedDatabaseNames = tenantItem.ConnectionStrings.Keys.Order(StringComparer.Ordinal).ToList()
        };
    }

    private string ProtectForCache(Guid tenantId, string name, string storedValue)
    {
        return _connectionStringProtector.IsProtected(storedValue)
            ? storedValue
            : _connectionStringProtector.Protect(tenantId, name, storedValue);
    }

    private static string NormalizeName(string? normalizedName, string fallbackName)
    {
        return (normalizedName ?? fallbackName).Trim().ToUpperInvariant();
    }

    private DistributedCacheEntryOptions CreateCacheOptions()
    {
        return new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = _cacheTtl };
    }

    private DistributedCacheEntryOptions CreateStampCacheOptions()
    {
        return new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = _stampCacheTtl };
    }
}
