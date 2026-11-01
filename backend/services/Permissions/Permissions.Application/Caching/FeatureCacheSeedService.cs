using Microsoft.EntityFrameworkCore;
using Permissions.Domain.FeatureManagement;
using Shared.Domain.Repositories;
using Shared.Modularity.DependencyInjection;

namespace Permissions.Application.Caching;

public class FeatureCacheSeedService : ITransientDependency
{
    private static readonly Microsoft.Extensions.Caching.Hybrid.HybridCacheEntryOptions StampCacheOptions = new()
    {
        Expiration = TimeSpan.FromHours(1)
    };
    private static readonly Guid DevelopmentDefaultTenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly IRepository<FeatureValue, Guid> _valueRepo;
    private readonly IRepository<FeatureDefinitionRecord, Guid> _definitionRepo;
    private readonly IRepository<FeatureProviderState, Guid> _providerStateRepo;
    private readonly TenantFeatureCacheSyncService _sync;
    private readonly PermissionProviderStampCache _stampCache;

    public FeatureCacheSeedService(
        IRepository<FeatureValue, Guid> valueRepo,
        IRepository<FeatureDefinitionRecord, Guid> definitionRepo,
        IRepository<FeatureProviderState, Guid> providerStateRepo,
        TenantFeatureCacheSyncService sync,
        PermissionProviderStampCache stampCache)
    {
        _valueRepo = valueRepo;
        _definitionRepo = definitionRepo;
        _providerStateRepo = providerStateRepo;
        _sync = sync;
        _stampCache = stampCache;
    }

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        var queryable = await _valueRepo.GetQueryableAsync();
        var tuples = await queryable
            .Where(x => x.ProviderName == "T")
            .Select(x => new { x.ProviderKey, x.Name })
            .Distinct()
            .ToListAsync(cancellationToken);

        var definitionQueryable = await _definitionRepo.GetQueryableAsync();
        var definitionNames = await definitionQueryable
            .Select(x => x.Name)
            .Distinct()
            .ToListAsync(cancellationToken);

        var providerStateQueryable = await _providerStateRepo.GetQueryableAsync();
        var providerStates = await providerStateQueryable
            .Where(x => x.ProviderName == "Feature")
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var providerStateByKey = providerStates.ToDictionary(
            state => (state.TenantId, state.ProviderKey),
            state => state.Stamp);
        var providerKeys = tuples
            .Where(x => !string.IsNullOrWhiteSpace(x.ProviderKey))
            .Select(x => x.ProviderKey!)
            .Concat(providerStateByKey.Keys.Select(x => x.ProviderKey))
            .ToHashSet(StringComparer.Ordinal);

        providerKeys.Add("host");
        if (IsDevelopment())
        {
            providerKeys.Add(DevelopmentDefaultTenantId.ToString("D"));
        }

        foreach (var providerKey in providerKeys)
        {
            var tenantId = string.Equals(providerKey, "host", StringComparison.Ordinal) ? (Guid?)null : Guid.Parse(providerKey);
            var names = definitionNames
                .Concat(tuples.Where(x => string.Equals(x.ProviderKey, providerKey, StringComparison.Ordinal)).Select(x => x.Name))
                .Distinct(StringComparer.Ordinal);

            await _sync.SyncAsync(tenantId, names, cancellationToken);

            var tenantPart = tenantId?.ToString("D") ?? "host";
            var key = PermissionCacheKeys.BuildProviderKey(tenantId, "Feature", tenantPart);
            var durableStamp = providerStateByKey.GetValueOrDefault((tenantId, providerKey));

            if (durableStamp > 0)
            {
                await _stampCache.SetAsync(
                    key,
                    new PermissionProviderStampCacheItem { Stamp = durableStamp },
                    options: StampCacheOptions,
                    considerUow: false,
                    token: cancellationToken);
            }
            else
            {
                // A missing durable provider row means version zero. Remove any legacy cache-only
                // timestamp so it cannot outrank the first transactional database stamp.
                await _stampCache.RemoveAsync(key, considerUow: false, token: cancellationToken);
            }
        }
    }

    private static bool IsDevelopment()
    {
        var environmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        return string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase);
    }
}
