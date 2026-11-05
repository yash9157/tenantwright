using Notifications.Domain.Shared.Enums;

namespace Notifications.Application.Contracts.RateLimiting;

public interface INotificationRateLimiter
{
    /// <summary>
    /// Records <paramref name="messageCount"/> against the (tenant, channel) and host-global windows
    /// and throws <see cref="NotificationRateLimitExceededException"/> if either quota is exceeded.
    /// No-op when rate limiting is disabled.
    /// </summary>
    Task EnsureWithinLimitAsync(
        Guid operationId,
        Guid? tenantId,
        NotificationChannel channel,
        int messageCount,
        CancellationToken cancellationToken = default);
}
