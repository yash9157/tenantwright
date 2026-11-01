using System.Security.Claims;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Permissions.Application.Contracts.Definitions;
using Shared.Caching;
using Shared.Caching.Abstractions;
using Shared.Caching.Implementations;
using Shared.Permissions.Authorization;
using Shared.Permissions.Caching;
using Shared.Permissions.Definitions;
using Shared.Threading;
using Shared.Uow.Abstractions;
using Shared.Uow.Options;
using Xunit;
using FrameworkDistributedCache = Microsoft.Extensions.Caching.Distributed.IDistributedCache;

namespace Shared.Permissions.Tests;

public sealed class ValkeyPermissionCheckerCacheMissTests
{
    [Fact]
    public async Task IsGrantedAsync_Does_not_cache_provider_grants_snapshot_or_decision_for_zero_stamp()
    {
        await using var provider = CreateServiceProvider();
        var stampCache = new RecordingStampCache(provider);
        var providerGrantsCache = new RecordingProviderGrantsCache(provider);
        var snapshotCache = new RecordingSnapshotCache(provider);
        var decisionCache = new RecordingDecisionCache(provider);
        var featureCache = new RecordingFeatureCache(provider);
        var definitions = new SharedPermissionDefinitionIndex(
            new SharedPermissionDefinitionManager(Array.Empty<PermissionDefinitionProvider>()));

        var checker = new ValkeyPermissionChecker(
            stampCache,
            providerGrantsCache,
            snapshotCache,
            decisionCache,
            featureCache,
            definitions,
            Options.Create(new RoleIdentityPermissionOptions()));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString("D")),
            new Claim(ClaimTypes.Role, "admin")
        ], "test"));

        var result = await checker.IsGrantedAsync(principal, "Identity.PlatformStaff");

        Assert.False(result);
        Assert.NotEmpty(stampCache.RemovedKeys);
        Assert.Empty(providerGrantsCache.GetOrCreateKeys);
        Assert.Empty(snapshotCache.GetOrCreateKeys);
        Assert.Empty(decisionCache.GetOrCreateKeys);
        Assert.DoesNotContain(providerGrantsCache.GetOrCreateKeys, key => key.Contains(":s:0", StringComparison.Ordinal));
    }

    private static ServiceProvider CreateServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddDistributedMemoryCache();
        services.AddHybridCache().AddSerializerFactory<SharedHybridCacheSerializerFactory>();
        services.AddOptions();
        services.Configure<SharedHybridCacheOptions>(options =>
        {
            options.HideErrors = false;
            options.GlobalHybridCacheEntryOptions = new HybridCacheEntryOptions
            {
                Expiration = TimeSpan.FromMinutes(5)
            };
        });
        services.Configure<SharedDistributedCacheOptions>(options => options.KeyPrefix = string.Empty);
        services.Configure<System.Text.Json.JsonSerializerOptions>(options =>
        {
            options.PropertyNameCaseInsensitive = true;
            options.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        });
        services.AddSingleton<ICacheTenantResolver, NullCacheTenantResolver>();
        services.AddSingleton<IDistributedCacheKeyNormalizer, DefaultDistributedCacheKeyNormalizer>();
        services.AddSingleton<ICancellationTokenProvider, TestCancellationTokenProvider>();
        services.AddSingleton<IUnitOfWorkManager, NoopUnitOfWorkManager>();

        return services.BuildServiceProvider();
    }

    private sealed class RecordingStampCache : PermissionProviderStampHybridCache
    {
        public RecordingStampCache(IServiceProvider provider)
            : base(provider,
                provider.GetRequiredService<IOptions<SharedHybridCacheOptions>>(),
                provider.GetRequiredService<HybridCache>(),
                provider.GetRequiredService<FrameworkDistributedCache>(),
                provider.GetRequiredService<ICancellationTokenProvider>(),
                provider.GetRequiredService<IDistributedCacheKeyNormalizer>(),
                provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<IUnitOfWorkManager>())
        {
        }

        public List<string> RemovedKeys { get; } = [];

        public override async Task<PermissionProviderStampCacheItem?> GetOrCreateAsync(
            string key,
            Func<Task<PermissionProviderStampCacheItem>> factory,
            Func<HybridCacheEntryOptions>? optionsFactory = null,
            bool? hideErrors = null,
            bool considerUow = false,
            CancellationToken token = default)
        {
            return await factory();
        }

        public override Task RemoveAsync(
            string key,
            bool? hideErrors = null,
            bool considerUow = false,
            CancellationToken token = default)
        {
            RemovedKeys.Add(key);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingProviderGrantsCache : PermissionProviderGrantsHybridCache
    {
        public RecordingProviderGrantsCache(IServiceProvider provider)
            : base(provider,
                provider.GetRequiredService<IOptions<SharedHybridCacheOptions>>(),
                provider.GetRequiredService<HybridCache>(),
                provider.GetRequiredService<FrameworkDistributedCache>(),
                provider.GetRequiredService<ICancellationTokenProvider>(),
                provider.GetRequiredService<IDistributedCacheKeyNormalizer>(),
                provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<IUnitOfWorkManager>())
        {
        }

        public List<string> GetOrCreateKeys { get; } = [];

        public override async Task<PermissionProviderGrantsCacheItem?> GetOrCreateAsync(
            string key,
            Func<Task<PermissionProviderGrantsCacheItem>> factory,
            Func<HybridCacheEntryOptions>? optionsFactory = null,
            bool? hideErrors = null,
            bool considerUow = false,
            CancellationToken token = default)
        {
            GetOrCreateKeys.Add(key);
            return await factory();
        }
    }

    private sealed class RecordingSnapshotCache : PermissionSnapshotHybridCache
    {
        public RecordingSnapshotCache(IServiceProvider provider)
            : base(provider,
                provider.GetRequiredService<IOptions<SharedHybridCacheOptions>>(),
                provider.GetRequiredService<HybridCache>(),
                provider.GetRequiredService<FrameworkDistributedCache>(),
                provider.GetRequiredService<ICancellationTokenProvider>(),
                provider.GetRequiredService<IDistributedCacheKeyNormalizer>(),
                provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<IUnitOfWorkManager>())
        {
        }

        public List<string> GetOrCreateKeys { get; } = [];

        public override async Task<PermissionSnapshotCacheItem?> GetOrCreateAsync(
            string key,
            Func<Task<PermissionSnapshotCacheItem>> factory,
            Func<HybridCacheEntryOptions>? optionsFactory = null,
            bool? hideErrors = null,
            bool considerUow = false,
            CancellationToken token = default)
        {
            GetOrCreateKeys.Add(key);
            return await factory();
        }
    }

    private sealed class RecordingDecisionCache : PermissionDecisionHybridCache
    {
        public RecordingDecisionCache(IServiceProvider provider)
            : base(provider,
                provider.GetRequiredService<IOptions<SharedHybridCacheOptions>>(),
                provider.GetRequiredService<HybridCache>(),
                provider.GetRequiredService<FrameworkDistributedCache>(),
                provider.GetRequiredService<ICancellationTokenProvider>(),
                provider.GetRequiredService<IDistributedCacheKeyNormalizer>(),
                provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<IUnitOfWorkManager>())
        {
        }

        public List<string> GetOrCreateKeys { get; } = [];

        public override async Task<PermissionDecisionCacheItem?> GetOrCreateAsync(
            string key,
            Func<Task<PermissionDecisionCacheItem>> factory,
            Func<HybridCacheEntryOptions>? optionsFactory = null,
            bool? hideErrors = null,
            bool considerUow = false,
            CancellationToken token = default)
        {
            GetOrCreateKeys.Add(key);
            return await factory();
        }
    }

    private sealed class RecordingFeatureCache : TenantFeatureValueHybridCache
    {
        public RecordingFeatureCache(IServiceProvider provider)
            : base(provider,
                provider.GetRequiredService<IOptions<SharedHybridCacheOptions>>(),
                provider.GetRequiredService<HybridCache>(),
                provider.GetRequiredService<FrameworkDistributedCache>(),
                provider.GetRequiredService<ICancellationTokenProvider>(),
                provider.GetRequiredService<IDistributedCacheKeyNormalizer>(),
                provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<IUnitOfWorkManager>())
        {
        }
    }

    private sealed class TestCancellationTokenProvider : ICancellationTokenProvider
    {
        public CancellationToken Token => CancellationToken.None;
    }

    private sealed class NoopUnitOfWorkManager : IUnitOfWorkManager
    {
        public IUnitOfWork? Current => null;
        public IUnitOfWork Begin(UnitOfWorkOptions options, bool requiresNew = false) => throw new NotSupportedException();
        public IUnitOfWork Begin(bool requiresNew = false) => throw new NotSupportedException();
        public IUnitOfWork Reserve(string reservationName, bool requiresNew = false) => throw new NotSupportedException();
        public void BeginReserved(string reservationName, UnitOfWorkOptions options) => throw new NotSupportedException();
        public bool TryBeginReserved(string reservationName, UnitOfWorkOptions options) => false;
    }
}
