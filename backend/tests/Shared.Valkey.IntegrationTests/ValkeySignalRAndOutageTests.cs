using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.BackgroundTasks.Workers;
using Shared.Caching;
using Shared.Caching.Sessions;
using Shared.DistributedLocking.Implementations;
using Shared.DistributedLocking.Options;
using Shared.MultiTenancy;
using Shared.Permissions.Caching;
using StackExchange.Redis;
using Xunit;

namespace Shared.Valkey.IntegrationTests;

[Collection(ValkeyCollection.Name)]
public sealed class ValkeySignalRAndOutageTests
{
    private readonly ValkeyContainerFixture _fixture;

    public ValkeySignalRAndOutageTests(ValkeyContainerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task SignalR_backplane_delivers_between_two_independent_servers()
    {
        var channelPrefix = $"{_fixture.KeyPrefix}signalr:{Guid.NewGuid():N}";
        await using var firstServer = await StartServerAsync(channelPrefix);
        await using var secondServer = await StartServerAsync(channelPrefix);
        await using var firstClient = CreateClient(firstServer.Url);
        await using var secondClient = CreateClient(secondServer.Url);
        var received = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        secondClient.On<string>("notification", payload => received.TrySetResult(payload));

        await firstClient.StartAsync();
        await secondClient.StartAsync();
        await firstClient.InvokeAsync("Broadcast", "cross-instance");

        Assert.Equal(
            "cross-instance",
            await received.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Real_outage_fails_cache_session_permission_and_lock_paths_closed_and_wake_uses_safety_poll()
    {
        var outageFixture = await _fixture.StartIndependentAsync();
        try
        {
            await using var connection = await outageFixture.CreateConnectionAsync();
            using var cache = ValkeyCacheSessionAndDataProtectionTests.CreateCache(
                connection,
                $"{outageFixture.KeyPrefix}outage:");
            var sessionStore = new ValkeySessionMarkerStore(cache);
            var permissionKey = PermissionCacheKeys.BuildProviderKey(
                Guid.NewGuid(),
                "Role",
                "outage-check");
            await cache.SetAsync(
                permissionKey,
                [1],
                new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1)
                });

            var lockOptions = new DistributedLockingOptions
            {
                Provider = "Valkey",
                KeyPrefix = outageFixture.KeyPrefix
            };
            var monitor = new StaticOptionsMonitor<DistributedLockingOptions>(lockOptions);
            var lockManager = new DistributedLockManager(
                new DefaultDistributedLockKeyNormalizer(new HostCurrentTenant(), monitor),
                monitor,
                NullLogger<DistributedLockManager>.Instance,
                connection);

            await outageFixture.StopAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            await Assert.ThrowsAnyAsync<RedisException>(
                () => cache.GetAsync(permissionKey));
            await Assert.ThrowsAnyAsync<RedisException>(
                () => sessionStore.IsActiveAsync(
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    Guid.NewGuid().ToString("D")));
            await Assert.ThrowsAsync<TimeoutException>(
                () => lockManager.AcquireAsync(
                    "outage-lock",
                    new DistributedLockAcquireOptions
                    {
                        AcquireTimeoutMs = 250,
                        LeaseMs = 1000
                    }));

            var wake = new BackgroundWorkerWakeSignal(
                NullLogger<BackgroundWorkerWakeSignal>.Instance,
                connection);
            var result = await wake.WaitAsync(
                "notifications:delivery",
                new BackgroundWorkerWakeOptions
                {
                    WakeupMode = BackgroundWorkerWakeMode.ValkeyPubSub,
                    ValkeyChannel = "tenantwright:integration:outage:wakeup",
                    SafetyPollTimeout = TimeSpan.FromMilliseconds(500)
                });
            Assert.Equal(BackgroundWorkerWakeResult.SafetyPollTimeout, result);
        }
        finally
        {
            await outageFixture.DisposeAsync();
        }
    }

    private async Task<RunningServer> StartServerAsync(string channelPrefix)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var configuration = ValkeyConnectionConfiguration.Create(
            _fixture.CreateConfiguration());
        configuration.ChannelPrefix = RedisChannel.Literal(channelPrefix);
        builder.Services
            .AddSignalR()
            .AddStackExchangeRedis(options => options.Configuration = configuration);

        var app = builder.Build();
        app.MapHub<TestNotificationsHub>("/hubs/notifications");
        await app.StartAsync();
        var server = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>();
        var address = server.Features
            .Get<IServerAddressesFeature>()!
            .Addresses
            .Single();
        return new RunningServer(app, address);
    }

    private static HubConnection CreateClient(string baseAddress)
    {
        return new HubConnectionBuilder()
            .WithUrl($"{baseAddress}/hubs/notifications")
            .Build();
    }

    public sealed class TestNotificationsHub : Hub
    {
        public Task Broadcast(string payload)
        {
            return Clients.All.SendAsync("notification", payload);
        }
    }

    private sealed class RunningServer : IAsyncDisposable
    {
        private readonly WebApplication _application;

        public RunningServer(WebApplication application, string url)
        {
            _application = application;
            Url = url;
        }

        public string Url { get; }

        public async ValueTask DisposeAsync()
        {
            await _application.StopAsync();
            await _application.DisposeAsync();
        }
    }

    private sealed class HostCurrentTenant : ICurrentTenant
    {
        public bool IsAvailable => false;

        public Guid? Id => null;

        public string? Name => null;

        public IDisposable Change(Guid? id, string? name = null) =>
            throw new NotSupportedException();
    }
}
