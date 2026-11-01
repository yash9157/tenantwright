using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.StackExchangeRedis;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shared.Caching.Implementations;
using Shared.Caching.Sessions;
using Xunit;

namespace Shared.Valkey.IntegrationTests;

[Collection(ValkeyCollection.Name)]
public sealed class ValkeyCacheSessionAndDataProtectionTests
{
    private readonly ValkeyContainerFixture _fixture;

    public ValkeyCacheSessionAndDataProtectionTests(ValkeyContainerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Cache_supports_cross_instance_batch_ttl_removal_and_namespace_isolation()
    {
        await using var firstConnection = await _fixture.CreateConnectionAsync();
        await using var secondConnection = await _fixture.CreateConnectionAsync();
        using var firstCache = CreateCache(firstConnection, $"{_fixture.KeyPrefix}cache:a:");
        using var secondCache = CreateCache(secondConnection, $"{_fixture.KeyPrefix}cache:a:");
        using var isolatedCache = CreateCache(secondConnection, $"{_fixture.KeyPrefix}cache:b:");

        var expiringOptions = new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(1)
        };
        await firstCache.SetManyAsync(
            new Dictionary<string, byte[]>
            {
                ["one"] = Encoding.UTF8.GetBytes("first"),
                ["two"] = Encoding.UTF8.GetBytes("second")
            },
            expiringOptions);
        await isolatedCache.SetAsync(
            "one",
            Encoding.UTF8.GetBytes("isolated"),
            new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1)
            });

        var values = await secondCache.GetManyAsync(["one", "two"]);
        Assert.Equal("first", Encoding.UTF8.GetString(values[0]!));
        Assert.Equal("second", Encoding.UTF8.GetString(values[1]!));
        Assert.Equal("isolated", Encoding.UTF8.GetString((await isolatedCache.GetAsync("one"))!));

        await firstCache.RemoveManyAsync(["one", "two"]);
        Assert.Null(await secondCache.GetAsync("one"));
        Assert.Null(await secondCache.GetAsync("two"));
        Assert.NotNull(await isolatedCache.GetAsync("one"));

        await firstCache.SetAsync(
            "expires",
            Encoding.UTF8.GetBytes("short-lived"),
            expiringOptions);
        await Task.Delay(TimeSpan.FromMilliseconds(1400));
        Assert.Null(await secondCache.GetAsync("expires"));
    }

    [Fact]
    public async Task Session_markers_are_tenant_isolated_visible_cross_instance_and_revocable()
    {
        await using var firstConnection = await _fixture.CreateConnectionAsync();
        await using var secondConnection = await _fixture.CreateConnectionAsync();
        using var firstCache = CreateCache(firstConnection, $"{_fixture.KeyPrefix}sessions:");
        using var secondCache = CreateCache(secondConnection, $"{_fixture.KeyPrefix}sessions:");
        var writer = new ValkeySessionMarkerStore(firstCache);
        var reader = new ValkeySessionMarkerStore(secondCache);
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var sessionId = Guid.NewGuid().ToString("D");

        await writer.SetActiveAsync(
            tenantA,
            userId,
            sessionId,
            "active",
            TimeSpan.FromMinutes(1));

        Assert.Equal("active", await reader.IsActiveAsync(tenantA, userId, sessionId));
        Assert.Null(await reader.IsActiveAsync(tenantB, userId, sessionId));

        await reader.RemoveAsync(tenantA, userId, sessionId);
        Assert.Null(await writer.IsActiveAsync(tenantA, userId, sessionId));
    }

    [Fact]
    public async Task Data_protection_keys_written_by_one_process_are_consumed_by_another()
    {
        await using var firstConnection = await _fixture.CreateConnectionAsync();
        await using var secondConnection = await _fixture.CreateConnectionAsync();
        var key = $"{_fixture.KeyPrefix}data-protection";

        await using var firstProvider = CreateDataProtectionProvider(firstConnection, key);
        var protectedPayload = firstProvider
            .GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("tenantwright-valkey-integration")
            .Protect("protected-value");

        await using var secondProvider = CreateDataProtectionProvider(secondConnection, key);
        var unprotected = secondProvider
            .GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("tenantwright-valkey-integration")
            .Unprotect(protectedPayload);

        Assert.Equal("protected-value", unprotected);
    }

    internal static ValkeyCache CreateCache(
        StackExchange.Redis.IConnectionMultiplexer connection,
        string instanceName)
    {
        var options = Options.Create(new RedisCacheOptions
        {
            InstanceName = instanceName,
            ConnectionMultiplexerFactory = () => Task.FromResult(connection)
        });
        return new ValkeyCache(options, connection);
    }

    private static ServiceProvider CreateDataProtectionProvider(
        StackExchange.Redis.IConnectionMultiplexer connection,
        string key)
    {
        var services = new ServiceCollection();
        services
            .AddDataProtection()
            .SetApplicationName("tenantwright-valkey-integration")
            .PersistKeysToStackExchangeRedis(connection, key);
        return services.BuildServiceProvider();
    }
}
