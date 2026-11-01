using Notifications.Application.Contracts.Common;

namespace Notifications.Application.Contracts.Presence;

public enum NotificationPresenceStatus
{
    Active = 1,
    Stale = 2,
    Inactive = 3
}

public class GetNotificationPresenceInput : PagedRequestDto
{
    public Guid? TenantId { get; set; }
    public string? Filter { get; set; }
    public NotificationPresenceStatus? Status { get; set; }
    public int? StaleAfterMinutes { get; set; }
}

public class NotificationPresenceSummaryDto
{
    public Guid? TenantId { get; set; }
    public int ActiveCount { get; set; }
    public int StaleCount { get; set; }
    public int InactiveCount { get; set; }
    public int TotalCount { get; set; }
    public DateTime? LatestHeartbeatAtUtc { get; set; }
    public int TtlSeconds { get; set; }
    public int StaleAfterMinutes { get; set; }
}

public class NotificationPresenceItemDto
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public string UserId { get; set; } = "";
    public NotificationPresenceStatus Status { get; set; }
    public DateTime LastSeenAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public long HeartbeatAgeSeconds { get; set; }
}

public interface INotificationPresenceQueryAppService
{
    Task<NotificationPresenceSummaryDto> GetSummaryAsync(
        GetNotificationPresenceInput input,
        CancellationToken cancellationToken = default);

    Task<PagedResultDto<NotificationPresenceItemDto>> GetListAsync(
        GetNotificationPresenceInput input,
        CancellationToken cancellationToken = default);
}
