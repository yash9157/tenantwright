using Notifications.Domain.Shared.Enums;

namespace Notifications.Application.Contracts.RateLimiting;

public interface INotificationRateLimitStore
{
    Task<NotificationQuotaReservation> ReserveAsync(NotificationQuotaRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record NotificationQuotaRequest(Guid OperationId, Guid? TenantId, NotificationChannel Channel,
    int Amount, int WindowSeconds, long TenantLimit, long GlobalLimit);

public enum NotificationQuotaDecision { Granted = 1, TenantDenied = 2, GlobalDenied = 3, ConflictingOperation = 4 }

public sealed record NotificationQuotaReservation(NotificationQuotaDecision Decision, long TenantCount,
    long GlobalCount, DateTime RetryAtUtc);
