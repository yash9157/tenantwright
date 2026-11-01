using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Internal;
using Shared.Caching.Implementations;
using Xunit;

namespace Shared.MultiTenancy.Tests;

public class DistributedCacheAdapterTests
{
    [Fact]
    public async Task Framework_adapter_supports_batch_operations_and_expiration()
    {
        // A manual clock keeps expiration deterministic; a wall-clock delay is flaky on a busy machine.
        var clock = new ManualSystemClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var services = new ServiceCollection();
        services.AddDistributedMemoryCache(options => options.Clock = clock);
        await using var provider = services.BuildServiceProvider();
        var adapter = new FrameworkDistributedCacheAdapter(
            provider.GetRequiredService<Microsoft.Extensions.Caching.Distributed.IDistributedCache>());

        await adapter.SetManyAsync(
            [
                new KeyValuePair<string, byte[]>("one", [1]),
                new KeyValuePair<string, byte[]>("two", [2])
            ],
            new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1)
            });

        var values = await adapter.GetManyAsync(["one", "two"]);
        Assert.Equal([1], values[0]);
        Assert.Equal([2], values[1]);

        clock.UtcNow = clock.UtcNow.AddMinutes(2);
        Assert.Equal([null, null], await adapter.GetManyAsync(["one", "two"]));
    }

    private sealed class ManualSystemClock(DateTimeOffset utcNow) : ISystemClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }

    [Fact]
    public async Task Framework_adapter_honors_cancellation()
    {
        var services = new ServiceCollection();
        services.AddDistributedMemoryCache();
        await using var provider = services.BuildServiceProvider();
        var adapter = new FrameworkDistributedCacheAdapter(
            provider.GetRequiredService<Microsoft.Extensions.Caching.Distributed.IDistributedCache>());
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => adapter.GetManyAsync(["one"], cancellation.Token));
    }
}
