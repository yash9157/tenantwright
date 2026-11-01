using Microsoft.EntityFrameworkCore;
using Permissions.Domain.FeatureManagement;
using Shared.Domain.Repositories;
using Shared.Modularity.DependencyInjection;

namespace Permissions.Application.Caching;

public class TenantFeatureCacheSyncService : ITransientDependency
{
    private static readonly Microsoft.Extensions.Caching.Hybrid.HybridCacheEntryOptions CacheOptions = new()
    {
        Expiration = TimeSpan.FromHours(1)
    };

    private readonly IRepository<FeatureValue, Guid> _valueRepo;
    private readonly IRepository<FeatureDefinitionRecord, Guid> _definitionRepo;
    private readonly TenantFeatureValueCache _cache;

    public TenantFeatureCacheSyncService(
        IRepository<FeatureValue, Guid> valueRepo,
        IRepository<FeatureDefinitionRecord, Guid> definitionRepo,
        TenantFeatureValueCache cache)
    {
        _valueRepo = valueRepo;
        _definitionRepo = definitionRepo;
        _cache = cache;
    }

    public async Task SyncAsync(Guid? tenantId, IEnumerable<string> featureNames, CancellationToken cancellationToken = default)
    {
        var tenantKey = tenantId?.ToString("D") ?? "host";
        var names = featureNames
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (names.Length == 0)
        {
            return;
        }

        var queryable = await _valueRepo.GetQueryableAsync();
        var values = await queryable
            .Where(x => x.ProviderName == "T" && x.ProviderKey == tenantKey && names.Contains(x.Name))
            .AsNoTracking()
            .Select(x => new { x.Name, x.Value })
            .ToListAsync(cancellationToken);

        var map = values.ToDictionary(x => x.Name, x => x.Value, StringComparer.Ordinal);

        var definitionQueryable = await _definitionRepo.GetQueryableAsync();
        var defaults = await definitionQueryable
            .Where(x => names.Contains(x.Name))
            .AsNoTracking()
            .Select(x => new { x.Name, x.DefaultValue })
            .ToListAsync(cancellationToken);
        var defaultMap = defaults.ToDictionary(x => x.Name, x => x.DefaultValue ?? "false", StringComparer.Ordinal);

        foreach (var name in names)
        {
            var key = FeatureCacheKeys.BuildTenantFeatureKey(tenantId, name);
            var value = ResolveFeatureValue(name, map, defaultMap);
            await _cache.SetAsync(
                key,
                new FeatureValueCacheItem { UpdatedAtUtc = DateTimeOffset.UtcNow, Value = value },
                options: CacheOptions,
                considerUow: true,
                token: cancellationToken);
        }
    }

    internal static string ResolveFeatureValue(
        string name,
        IReadOnlyDictionary<string, string> explicitValues,
        IReadOnlyDictionary<string, string> defaultValues)
    {
        if (explicitValues.TryGetValue(name, out var explicitValue))
        {
            return explicitValue;
        }

        return defaultValues.TryGetValue(name, out var defaultValue) ? defaultValue : "false";
    }
}
