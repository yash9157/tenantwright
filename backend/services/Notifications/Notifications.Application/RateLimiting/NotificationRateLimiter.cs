using System.Diagnostics.Metrics;
using Microsoft.Extensions.Options;
using Notifications.Application.Contracts.RateLimiting;
using Notifications.Domain.Shared.Enums;
using Shared.Modularity.DependencyInjection;

namespace Notifications.Application.RateLimiting;

public sealed class NotificationRateLimiter : INotificationRateLimiter, ITransientDependency
{
    private static readonly Meter Meter = new("Notifications.RateLimit");
    private static readonly Counter<long> RejectedCounter =
        Meter.CreateCounter<long>("notifications.ratelimit.rejected");

    private readonly INotificationRateLimitStore _store;
    private readonly IOptionsMonitor<NotificationsRateLimitOptions> _options;

    public NotificationRateLimiter(
        INotificationRateLimitStore store,
        IOptionsMonitor<NotificationsRateLimitOptions> options)
    {
        _store = store;
        _options = options;
    }

    public async Task EnsureWithinLimitAsync(
        Guid? tenantId,
        NotificationChannel channel,
        int messageCount,
        CancellationToken cancellationToken = default)
    {
        var options = _options.CurrentValue;
        if (!options.Enabled || messageCount <= 0)
        {
            return;
        }

        var window = TimeSpan.FromSeconds(Math.Clamp(options.WindowSeconds, 1, 3600));

        var perChannelBase = options.PerChannelOverrides.TryGetValue(channel.ToString(), out var overrideLimit)
            ? overrideLimit
            : options.PerTenantChannelPerWindow;
        var tenantLimit = Math.Max(0, perChannelBase) + Math.Max(0, options.BurstAllowance);

        if (tenantLimit > 0)
        {
            var tenantKey = $"notifications:ratelimit:tenant:{tenantId?.ToString() ?? "host"}:{channel}";
            var tenantCount = await _store.IncrementAndGetAsync(tenantKey, window, messageCount, cancellationToken);
            if (tenantCount > tenantLimit)
            {
                RejectedCounter.Add(1,
                    new KeyValuePair<string, object?>("scope", "tenant"),
                    new KeyValuePair<string, object?>("channel", channel.ToString()));
                throw new NotificationRateLimitExceededException("tenant", channel, tenantLimit, tenantCount);
            }
        }

        if (options.GlobalPerWindow > 0)
        {
            var globalCount = await _store.IncrementAndGetAsync(
                "notifications:ratelimit:global", window, messageCount, cancellationToken);
            if (globalCount > options.GlobalPerWindow)
            {
                RejectedCounter.Add(1,
                    new KeyValuePair<string, object?>("scope", "global"),
                    new KeyValuePair<string, object?>("channel", channel.ToString()));
                throw new NotificationRateLimitExceededException("global", channel, options.GlobalPerWindow, globalCount);
            }
        }
    }
}
