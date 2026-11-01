using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.BackgroundTasks.Workers;
using Shared.DistributedLocking.Implementations;
using Shared.DistributedLocking.Options;
using Shared.MultiTenancy;
using Shared.Permissions.Caching;
using Xunit;

namespace Shared.Valkey.IntegrationTests;

[Collection(ValkeyCollection.Name)]
public sealed class ValkeyLockPermissionAndWakeTests
{
    private readonly ValkeyContainerFixture _fixture;

    public ValkeyLockPermissionAndWakeTests(ValkeyContainerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Distributed_lock_has_one_owner_and_releases_for_the_next_instance()
    {
        await using var firstConnection = await _fixture.CreateConnectionAsync();
        await using var secondConnection = await _fixture.CreateConnectionAsync();
        var options = new DistributedLockingOptions
        {
            Provider = "Valkey",
            KeyPrefix = _fixture.KeyPrefix,
            Valkey =
            {
                BusyWaitMinMs = 10,
                BusyWaitMaxMs = 25
            }
        };
        var monitor = new StaticOptionsMonitor<DistributedLockingOptions>(options);
        var tenant = new FixedCurrentTenant(Guid.NewGuid());
        var normalizer = new DefaultDistributedLockKeyNormalizer(tenant, monitor);
        var firstManager = new DistributedLockManager(
            normalizer,
            monitor,
            NullLogger<DistributedLockManager>.Instance,
            firstConnection);
        var secondManager = new DistributedLockManager(
            normalizer,
            monitor,
            NullLogger<DistributedLockManager>.Instance,
            secondConnection);
        var acquire = new DistributedLockAcquireOptions
        {
            AcquireTimeoutMs = 150,
            LeaseMs = 3000
        };

        await using (await firstManager.AcquireAsync("permission-refresh", acquire))
        {
            Assert.Null(await secondManager.TryAcquireAsync("permission-refresh", acquire));
        }

        await using var nextOwner = await secondManager.AcquireAsync(
            "permission-refresh",
            acquire);
        Assert.NotNull(nextOwner);
    }

    [Fact]
    public async Task Replication_acl_can_ping_but_cannot_read_or_write_application_keys()
    {
        Assert.Equal(
            "+PONG",
            await _fixture.ExecuteAsReplicationUserAsync("PING"));

        var writeResult = await _fixture.ExecuteAsReplicationUserAsync(
            "SET",
            $"{_fixture.KeyPrefix}replication-user-forbidden",
            "value");
        Assert.StartsWith("-NOPERM", writeResult, StringComparison.Ordinal);

        var readResult = await _fixture.ExecuteAsReplicationUserAsync(
            "GET",
            $"{_fixture.KeyPrefix}replication-user-forbidden");
        Assert.StartsWith("-NOPERM", readResult, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Permission_cache_is_cross_instance_tenant_isolated_and_invalidatable()
    {
        await using var firstConnection = await _fixture.CreateConnectionAsync();
        await using var secondConnection = await _fixture.CreateConnectionAsync();
        await using var thirdConnection = await _fixture.CreateConnectionAsync();
        await using var firstProvider = CreatePermissionCacheProvider(firstConnection, "a");
        await using var secondProvider = CreatePermissionCacheProvider(secondConnection, "a");
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var tenantAKey = PermissionCacheKeys.BuildProviderKey(tenantA, "Role", "care-team");
        var tenantBKey = PermissionCacheKeys.BuildProviderKey(tenantB, "Role", "care-team");
        var firstCache = firstProvider.GetRequiredService<HybridCache>();
        await firstCache.SetAsync(
            tenantAKey,
            new PermissionProviderStampCacheItem { Stamp = 11, UpdatedAtUtc = DateTimeOffset.UtcNow });
        await firstCache.SetAsync(
            tenantBKey,
            new PermissionProviderStampCacheItem { Stamp = 22, UpdatedAtUtc = DateTimeOffset.UtcNow });

        var secondCache = secondProvider.GetRequiredService<HybridCache>();
        var tenantAItem = await secondCache.GetOrCreateAsync(
            tenantAKey,
            _ => ValueTask.FromResult(new PermissionProviderStampCacheItem { Stamp = -1 }));
        var tenantBItem = await secondCache.GetOrCreateAsync(
            tenantBKey,
            _ => ValueTask.FromResult(new PermissionProviderStampCacheItem { Stamp = -1 }));
        Assert.Equal(11, tenantAItem.Stamp);
        Assert.Equal(22, tenantBItem.Stamp);

        await firstCache.RemoveAsync(tenantAKey);
        await using var thirdProvider = CreatePermissionCacheProvider(thirdConnection, "a");
        var thirdCache = thirdProvider.GetRequiredService<HybridCache>();
        var tenantAAfterInvalidation = await thirdCache.GetOrCreateAsync(
            tenantAKey,
            _ => ValueTask.FromResult(new PermissionProviderStampCacheItem { Stamp = 33 }));
        var tenantBAfterInvalidation = await thirdCache.GetOrCreateAsync(
            tenantBKey,
            _ => ValueTask.FromResult(new PermissionProviderStampCacheItem { Stamp = -1 }));

        Assert.Equal(33, tenantAAfterInvalidation.Stamp);
        Assert.Equal(22, tenantBAfterInvalidation.Stamp);
        Assert.NotEqual(tenantAKey, tenantBKey);
    }

    [Fact]
    public async Task Notification_wake_published_by_one_instance_wakes_another()
    {
        await using var firstConnection = await _fixture.CreateConnectionAsync();
        await using var secondConnection = await _fixture.CreateConnectionAsync();
        var publisher = new BackgroundWorkerWakeSignal(
            NullLogger<BackgroundWorkerWakeSignal>.Instance,
            firstConnection);
        var waiter = new BackgroundWorkerWakeSignal(
            NullLogger<BackgroundWorkerWakeSignal>.Instance,
            secondConnection);
        var wakeKey = $"{_fixture.KeyPrefix}notifications:delivery";
        var options = new BackgroundWorkerWakeOptions
        {
            WakeupMode = BackgroundWorkerWakeMode.ValkeyPubSub,
            ValkeyChannel = $"{_fixture.KeyPrefix}notifications:delivery:wakeup",
            SafetyPollTimeout = TimeSpan.FromSeconds(5)
        };

        var wait = waiter.WaitAsync(wakeKey, options);
        await Task.Delay(250);
        await publisher.SignalAsync(wakeKey, options);

        Assert.Equal(BackgroundWorkerWakeResult.Signal, await wait);
    }

    private ServiceProvider CreatePermissionCacheProvider(
        StackExchange.Redis.IConnectionMultiplexer connection,
        string namespaceSuffix)
    {
        var services = new ServiceCollection();
        var distributedCache = ValkeyCacheSessionAndDataProtectionTests.CreateCache(
            connection,
            $"{_fixture.KeyPrefix}permissions:{namespaceSuffix}:");
        services.AddSingleton<IDistributedCache>(distributedCache);
        services.AddHybridCache();
        return services.BuildServiceProvider();
    }

    private sealed class FixedCurrentTenant : ICurrentTenant
    {
        public FixedCurrentTenant(Guid? id)
        {
            Id = id;
        }

        public bool IsAvailable => Id.HasValue;

        public Guid? Id { get; private set; }

        public string? Name => null;

        public IDisposable Change(Guid? id, string? name = null)
        {
            var previous = Id;
            Id = id;
            return new Restore(() => Id = previous);
        }

        private sealed class Restore : IDisposable
        {
            private readonly Action _restore;

            public Restore(Action restore)
            {
                _restore = restore;
            }

            public void Dispose() => _restore();
        }
    }
}
