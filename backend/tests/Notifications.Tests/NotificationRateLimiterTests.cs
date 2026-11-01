using Microsoft.Extensions.Options;
using Notifications.Application.Contracts.RateLimiting;
using Notifications.Application.RateLimiting;
using Notifications.Domain.Shared.Enums;
using Xunit;

namespace Notifications.Tests;

public class NotificationRateLimiterTests
{
    private static readonly Guid Tenant = Guid.NewGuid();

    [Fact]
    public async Task Disabled_limiter_never_throws()
    {
        var limiter = Build(new NotificationsRateLimitOptions { Enabled = false, PerTenantChannelPerWindow = 1 });

        // Well over any limit, but disabled -> no-op.
        await limiter.EnsureWithinLimitAsync(Tenant, NotificationChannel.Email, 1000);
    }

    [Fact]
    public async Task Allows_up_to_the_per_tenant_channel_limit()
    {
        var limiter = Build(new NotificationsRateLimitOptions
        {
            Enabled = true,
            PerTenantChannelPerWindow = 5,
            GlobalPerWindow = 0
        });

        await limiter.EnsureWithinLimitAsync(Tenant, NotificationChannel.Email, 5);
    }

    [Fact]
    public async Task Rejects_when_per_tenant_channel_limit_exceeded()
    {
        var limiter = Build(new NotificationsRateLimitOptions
        {
            Enabled = true,
            PerTenantChannelPerWindow = 5,
            GlobalPerWindow = 0
        });

        await limiter.EnsureWithinLimitAsync(Tenant, NotificationChannel.Email, 5);

        var ex = await Assert.ThrowsAsync<NotificationRateLimitExceededException>(
            () => limiter.EnsureWithinLimitAsync(Tenant, NotificationChannel.Email, 1));
        Assert.Equal("tenant", ex.Scope);
        Assert.Equal(NotificationChannel.Email, ex.Channel);
        Assert.Equal(5, ex.Limit);
    }

    [Fact]
    public async Task Per_channel_override_and_burst_allowance_apply()
    {
        var limiter = Build(new NotificationsRateLimitOptions
        {
            Enabled = true,
            PerTenantChannelPerWindow = 5,
            BurstAllowance = 2,
            GlobalPerWindow = 0,
            PerChannelOverrides = { ["Sms"] = 1 }
        });

        // Sms override (1) + burst (2) = 3 allowed.
        await limiter.EnsureWithinLimitAsync(Tenant, NotificationChannel.Sms, 3);
        await Assert.ThrowsAsync<NotificationRateLimitExceededException>(
            () => limiter.EnsureWithinLimitAsync(Tenant, NotificationChannel.Sms, 1));
    }

    [Fact]
    public async Task Counters_are_isolated_per_tenant_and_channel()
    {
        var other = Guid.NewGuid();
        var limiter = Build(new NotificationsRateLimitOptions
        {
            Enabled = true,
            PerTenantChannelPerWindow = 2,
            GlobalPerWindow = 0
        });

        await limiter.EnsureWithinLimitAsync(Tenant, NotificationChannel.Email, 2);
        // Different channel and different tenant have their own budgets.
        await limiter.EnsureWithinLimitAsync(Tenant, NotificationChannel.Sms, 2);
        await limiter.EnsureWithinLimitAsync(other, NotificationChannel.Email, 2);
    }

    [Fact]
    public async Task Rejects_when_global_cap_exceeded_even_within_tenant_limit()
    {
        var limiter = Build(new NotificationsRateLimitOptions
        {
            Enabled = true,
            PerTenantChannelPerWindow = 1000,
            GlobalPerWindow = 3
        });

        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await limiter.EnsureWithinLimitAsync(a, NotificationChannel.Email, 2);

        var ex = await Assert.ThrowsAsync<NotificationRateLimitExceededException>(
            () => limiter.EnsureWithinLimitAsync(b, NotificationChannel.Email, 2));
        Assert.Equal("global", ex.Scope);
        Assert.Equal(3, ex.Limit);
    }

    private static NotificationRateLimiter Build(NotificationsRateLimitOptions options)
    {
        return new NotificationRateLimiter(new FakeStore(), new StaticOptionsMonitor<NotificationsRateLimitOptions>(options));
    }

    private sealed class FakeStore : INotificationRateLimitStore
    {
        private readonly Dictionary<string, long> _counts = new(StringComparer.Ordinal);

        public Task<long> IncrementAndGetAsync(string key, TimeSpan window, int amount, CancellationToken cancellationToken = default)
        {
            _counts.TryGetValue(key, out var current);
            current += amount;
            _counts[key] = current;
            return Task.FromResult(current);
        }
    }

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StaticOptionsMonitor(T value) => CurrentValue = value;
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
