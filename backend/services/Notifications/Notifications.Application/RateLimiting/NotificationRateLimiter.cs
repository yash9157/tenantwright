using System.Diagnostics.Metrics;
using Microsoft.Extensions.Options;
using Notifications.Application.Contracts.RateLimiting;
using Notifications.Domain.Shared.Constants;
using Notifications.Domain.Shared.Enums;
using Shared;
using Shared.Modularity.DependencyInjection;

namespace Notifications.Application.RateLimiting;

public sealed class NotificationRateLimiter(INotificationRateLimitStore store,
    IOptionsMonitor<NotificationsRateLimitOptions> options) : INotificationRateLimiter, ITransientDependency
{
    private static readonly Meter Meter = new("Notifications.RateLimit");
    private static readonly Counter<long> RejectedCounter = Meter.CreateCounter<long>("notifications.ratelimit.rejected");

    public async Task EnsureWithinLimitAsync(Guid operationId, Guid? tenantId, NotificationChannel channel,
        int messageCount, CancellationToken cancellationToken = default)
    {
        var configured = options.CurrentValue;
        if (!configured.Enabled)
            return;
        if (operationId == Guid.Empty || tenantId == Guid.Empty || messageCount <= 0 || !Enum.IsDefined(channel))
            throw new ArgumentException("A quota reservation requires stable operation identity, scope, channel and positive amount.");
        var perChannel = configured.PerChannelOverrides.TryGetValue(channel.ToString(), out var channelLimit)
            ? channelLimit : configured.PerTenantChannelPerWindow;
        var tenantLimit = (long)Math.Max(0, perChannel) + Math.Max(0, configured.BurstAllowance);
        var result = await store.ReserveAsync(new NotificationQuotaRequest(operationId, tenantId, channel,
            messageCount, Math.Clamp(configured.WindowSeconds, 1, 3600), tenantLimit,
            Math.Max(0, configured.GlobalPerWindow)), cancellationToken);
        if (result.Decision == NotificationQuotaDecision.Granted)
            return;
        if (result.Decision == NotificationQuotaDecision.ConflictingOperation)
            throw new SharedConflictException(NotificationErrorCodes.DispatchRequestConflict,
                "This quota operation already represents different channel or amount in this scope.");
        var scope = result.Decision == NotificationQuotaDecision.TenantDenied ? "tenant" : "global";
        RejectedCounter.Add(1, new KeyValuePair<string, object?>("scope", scope),
            new KeyValuePair<string, object?>("channel", channel.ToString()));
        throw new NotificationRateLimitExceededException(scope, channel,
            scope == "tenant" ? tenantLimit : configured.GlobalPerWindow,
            scope == "tenant" ? result.TenantCount : result.GlobalCount, result.RetryAtUtc);
    }
}
