using System.Security.Cryptography;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Options;
using Notifications.Application.Contracts.RateLimiting;
using Notifications.Application.RateLimiting;
using Notifications.Domain.Shared.Enums;
using StackExchange.Redis;
using Xunit;

namespace Notifications.Tests;

[Collection("notifications-valkey")]
[Trait("Category", "ValkeyIntegration")]
public sealed class NotificationQuotaReservationTests(NotificationQuotaValkeyFixture infrastructure)
{
    [Fact]
    public async Task Concurrent_replicas_share_one_atomic_tenant_and_global_budget()
    {
        var instance = Guid.NewGuid().ToString("N");
        var first = Store(instance, infrastructure.FirstConnection);
        var second = Store(instance, infrastructure.SecondConnection);
        var tenantId = Guid.NewGuid();
        var results = await Task.WhenAll(Enumerable.Range(0, 100).Select(index =>
            (index % 2 == 0 ? first : second).ReserveAsync(Request(tenantId, tenantLimit: 17, globalLimit: 23))));
        Assert.Equal(17, results.Count(result => result.Decision == NotificationQuotaDecision.Granted));
        Assert.All(results.Where(result => result.Decision != NotificationQuotaDecision.Granted),
            result => Assert.Equal(NotificationQuotaDecision.TenantDenied, result.Decision));
        var otherScope = await second.ReserveAsync(Request(Guid.NewGuid(), amount: 6, tenantLimit: 17, globalLimit: 23));
        Assert.Equal(NotificationQuotaDecision.Granted, otherScope.Decision);
        Assert.Equal(NotificationQuotaDecision.GlobalDenied,
            (await first.ReserveAsync(Request(null, tenantLimit: 17, globalLimit: 23))).Decision);
    }

    [Fact]
    public async Task Concurrent_redelivery_charges_once_and_conflicting_binding_fails_closed()
    {
        var instance = Guid.NewGuid().ToString("N");
        var first = Store(instance, infrastructure.FirstConnection);
        var second = Store(instance, infrastructure.SecondConnection);
        var request = Request(Guid.NewGuid(), amount: 5, tenantLimit: 5, globalLimit: 5);
        var results = await Task.WhenAll(Enumerable.Range(0, 50).Select(index =>
            (index % 2 == 0 ? first : second).ReserveAsync(request)));
        Assert.All(results, result => Assert.Equal(NotificationQuotaDecision.Granted, result.Decision));
        Assert.Equal(NotificationQuotaDecision.ConflictingOperation, (await second.ReserveAsync(request with { Amount = 1 })).Decision);
        Assert.Equal(NotificationQuotaDecision.ConflictingOperation, (await second.ReserveAsync(request with { Channel = NotificationChannel.Sms })).Decision);
        Assert.Equal(NotificationQuotaDecision.TenantDenied, (await first.ReserveAsync(request with { OperationId = Guid.NewGuid(), Amount = 1 })).Decision);
    }

    [Fact]
    public async Task Global_denial_does_not_consume_the_tenant_budget()
    {
        var store = Store(Guid.NewGuid().ToString("N"), infrastructure.FirstConnection);
        Assert.Equal(NotificationQuotaDecision.Granted,
            (await store.ReserveAsync(Request(Guid.NewGuid(), amount: 2, tenantLimit: 10, globalLimit: 2))).Decision);
        var denied = Request(Guid.NewGuid(), amount: 1, tenantLimit: 1, globalLimit: 2);
        Assert.Equal(NotificationQuotaDecision.GlobalDenied, (await store.ReserveAsync(denied)).Decision);
        var acceptedAfterCapacityChange = await store.ReserveAsync(denied with { GlobalLimit = 3 });
        Assert.Equal(NotificationQuotaDecision.Granted, acceptedAfterCapacityChange.Decision);
        Assert.Equal(1, acceptedAfterCapacityChange.TenantCount);
    }

    [Fact]
    public async Task A_granted_operation_is_not_charged_again_after_window_rollover()
    {
        var store = Store(Guid.NewGuid().ToString("N"), infrastructure.FirstConnection);
        var request = Request(null, tenantLimit: 1, globalLimit: 1) with { WindowSeconds = 1 };
        Assert.Equal(NotificationQuotaDecision.Granted, (await store.ReserveAsync(request)).Decision);
        await Task.Delay(TimeSpan.FromMilliseconds(1100));
        Assert.Equal(NotificationQuotaDecision.Granted, (await store.ReserveAsync(request)).Decision);
        Assert.Equal(NotificationQuotaDecision.Granted, (await store.ReserveAsync(request with { OperationId = Guid.NewGuid() })).Decision);
    }

    private static NotificationQuotaRequest Request(Guid? tenantId, int amount = 1, long tenantLimit = 100, long globalLimit = 100) =>
        new(Guid.NewGuid(), tenantId, NotificationChannel.Email, amount, 3600, tenantLimit, globalLimit);

    private static ValkeyNotificationRateLimitStore Store(string instance, IConnectionMultiplexer connection) =>
        new([connection], Options.Create(new RedisCacheOptions { InstanceName = instance }));
}

public sealed class NotificationQuotaValkeyFixture : IAsyncLifetime
{
    // Match the digest-pinned image already used by Shared.Valkey.IntegrationTests.
    private const string Image = "valkey/valkey:9.1.1@sha256:3acc0687f2a2e1091fae6450d7842dd658c941338cf0a873ddd9e14b9e4ea4dd";
    private readonly string _password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    private IContainer? _container;
    public IConnectionMultiplexer FirstConnection { get; private set; } = null!;
    public IConnectionMultiplexer SecondConnection { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _container = new ContainerBuilder(Image).WithName($"tenantwright-notification-quotas-test-{Guid.NewGuid():N}")
            .WithPortBinding(6379, true)
            .WithCommand("valkey-server", "--requirepass", _password, "--save", "", "--appendonly", "no")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Ready to accept connections"))
            .Build();
        using var startup = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _container.StartAsync(startup.Token);
        var configuration = new ConfigurationOptions
        {
            Password = _password, AbortOnConnectFail = true, ConnectTimeout = 10000, SyncTimeout = 10000
        };
        configuration.EndPoints.Add("127.0.0.1", _container.GetMappedPublicPort(6379));
        FirstConnection = await ConnectionMultiplexer.ConnectAsync(configuration).WaitAsync(startup.Token);
        SecondConnection = await ConnectionMultiplexer.ConnectAsync(configuration).WaitAsync(startup.Token);
    }

    public async Task DisposeAsync()
    {
        if (FirstConnection is not null) await FirstConnection.DisposeAsync();
        if (SecondConnection is not null) await SecondConnection.DisposeAsync();
        if (_container is not null) await _container.DisposeAsync();
    }
}

[CollectionDefinition("notifications-valkey", DisableParallelization = true)]
public sealed class NotificationQuotaValkeySuite : ICollectionFixture<NotificationQuotaValkeyFixture>;
