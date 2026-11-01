using Notifications.Domain.Shared.Constants;
using Notifications.Domain.Shared.Enums;
using Notifications.Application.Contracts.Common;

namespace Notifications.Application.Contracts.Notifications;

public class GetNotificationsInput : PagedRequestDto
{
    public Guid? TenantId { get; set; }
    public NotificationChannel? Channel { get; set; }
    public NotificationStatus? Status { get; set; }
    public string? TemplateName { get; set; }
    public DateTime? FromUtc { get; set; }
    public DateTime? ToUtc { get; set; }
}

public class NotificationListItemDto
{
    public Guid Id { get; set; }
    public Guid RequestId { get; set; }
    public Guid? TenantId { get; set; }
    public NotificationChannel Channel { get; set; }
    public string TemplateName { get; set; } = "";
    public string SourceService { get; set; } = "";
    public string? Subject { get; set; }
    public string? Category { get; set; }
    public NotificationPriority Priority { get; set; }
    public string? ActionUrl { get; set; }
    public DateTime CreationTime { get; set; }
    public DateTime? ScheduledForUtc { get; set; }
    public int DeliveryCount { get; set; }
    public int QueuedCount { get; set; }
    public int ProcessingCount { get; set; }
    public int ProviderAcceptedCount { get; set; }
    public int DeliveredCount { get; set; }
    public int FailedCount { get; set; }
    public int NeedsReviewCount { get; set; }
    public int DeadLetteredCount { get; set; }
}

public sealed class NotificationRecoverySummaryDto
{
    public string SourceService { get; set; } = "";
    public string? Category { get; set; }
    public string? ActionUrl { get; set; }
    public NotificationStatus Status { get; set; }
    public int Count { get; set; }
    public DateTime OldestAtUtc { get; set; }
    public DateTime LatestAtUtc { get; set; }
    public string SafeReason { get; set; } = "";
    public bool CanRetry { get; set; }
}

public class NotificationRedactionMetadataDto
{
    public bool RedactionApplied { get; set; }
    public int WithheldFieldCount { get; set; }
    public List<string> WithheldFields { get; set; } = [];
}

public class NotificationDto
{
    public Guid Id { get; set; }
    public Guid RequestId { get; set; }
    public Guid? TenantId { get; set; }
    public NotificationChannel Channel { get; set; }
    public string TemplateName { get; set; } = "";
    public string SourceService { get; set; } = "";
    public string TemplateModelJson { get; set; } = "";
    public NotificationRedactionMetadataDto Redaction { get; set; } = new();
    public string? Subject { get; set; }
    public string? Category { get; set; }
    public NotificationPriority Priority { get; set; }
    public string? ActionUrl { get; set; }
    public DateTime CreationTime { get; set; }
    public DateTime? ScheduledForUtc { get; set; }
}

public class GetNotificationDeliveryChronologyInput : PagedRequestDto
{
    /// <summary>
    /// Host callers only: the tenant that owns the delivery. Delivery ids are unique per database, so a host lookup of
    /// an id present in several tenant databases requires it; tenant callers are always scoped to their own tenant.
    /// </summary>
    public Guid? TenantId { get; set; }
}

public class NotificationDeliveryChronologyItemDto
{
    public long Id { get; set; }
    public long DeliveryId { get; set; }
    public NotificationDeliveryEventKind EventKind { get; set; }
    public NotificationStatus Status { get; set; }
    public string? ProviderMessageId { get; set; }
    public string? ResponseCode { get; set; }
    public string? SafeError { get; set; }
    public long? LatencyMs { get; set; }
    public string? ActorId { get; set; }
    public NotificationDeliveryActorType ActorType { get; set; }
    public DateTime OccurredAtUtc { get; set; }
}

public class NotificationDeliveryDto
{
    public long Id { get; set; }
    public Guid NotificationId { get; set; }
    public long RecipientId { get; set; }
    public NotificationRecipientType RecipientType { get; set; }
    public string RecipientValue { get; set; } = "";
    public NotificationStatus Status { get; set; }
    public string? ProviderMessageId { get; set; }
    public string? LastError { get; set; }
    public int TryCount { get; set; }
    public DateTime? LastTryTimeUtc { get; set; }
    public DateTime? SentTimeUtc { get; set; }
    public DateTime? NextAttemptTimeUtc { get; set; }
    public DateTime? NotBeforeUtc { get; set; }
    public DateTime? PolicyHeldUntilUtc { get; set; }
    public Guid? DeliveryAttemptId { get; set; }
    public DateTime? DeliveryAttemptedAtUtc { get; set; }
    public DateTime CreationTime { get; set; }
}
