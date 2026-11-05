using Notifications.Domain.Shared.Constants;
using Notifications.Domain.Shared.Enums;
using Shared.Domain;
using Shared.Domain.Entities.Auditing;
using Shared.AuditLogs.Auditing;

namespace Notifications.Domain.Notifications;

public class Notification : FullAuditedAggregateRoot<Guid>, IMultiTenant
{
    public Guid? TenantId { get; private set; }

    public Guid RequestId { get; private set; }
    public Guid? DispatchStepId { get; private set; }
    public Guid? DispatchScopeId { get; private set; }

    public NotificationChannel Channel { get; private set; }

    public string TemplateName { get; private set; } = default!;

    public string TemplateModelJson { get; private set; } = "{}";

    public string SourceService { get; private set; } = "notifications";

    public string? Subject { get; private set; }

    public string? Category { get; private set; }

    public NotificationPriority Priority { get; private set; } = NotificationPriority.Normal;

    public string? ActionUrl { get; private set; }

    /// <summary>The original one-time schedule requested for this notification, if any.</summary>
    public DateTime? ScheduledForUtc { get; private set; }

    /// <summary>User whose local preference applies when the recipient value is not itself a user id.</summary>
    public string? PreferenceUserId { get; private set; }

    public virtual ICollection<NotificationRecipient> Recipients { get; private set; } = new List<NotificationRecipient>();

    public virtual ICollection<NotificationDelivery> Deliveries { get; private set; } = new List<NotificationDelivery>();

    public virtual ICollection<UserNotification> UserNotifications { get; private set; } = new List<UserNotification>();

    protected Notification()
    {
    }

    public Notification(
        Guid id,
        Guid? tenantId,
        Guid? requestId,
        NotificationChannel channel,
        string templateName,
        string templateModelJson,
        string? subject = null,
        string? category = null,
        NotificationPriority priority = NotificationPriority.Normal,
        string? actionUrl = null,
        DateTime? scheduledForUtc = null,
        string? preferenceUserId = null,
        string sourceService = "notifications")
        : base(id)
    {
        TenantId = tenantId;
        RequestId = requestId ?? Guid.CreateVersion7();
        Channel = channel;
        TemplateName = global::Shared.Check.NotNullOrWhiteSpace(templateName, nameof(templateName), maxLength: 256);
        TemplateModelJson = global::Shared.Check.NotNullOrWhiteSpace(templateModelJson, nameof(templateModelJson));
        SourceService = global::Shared.Check.NotNullOrWhiteSpace(
            sourceService,
            nameof(sourceService),
            maxLength: 128).Trim().ToLowerInvariant();
        var normalizedSubject = subject?.Trim();
        if (normalizedSubject is { Length: > 256 })
        {
            throw new ArgumentException("subject length must be equal to or lower than 256!", nameof(subject));
        }

        Subject = string.IsNullOrWhiteSpace(normalizedSubject) ? null : normalizedSubject;

        var normalizedCategory = category?.Trim();
        if (normalizedCategory is { Length: > 128 })
        {
            throw new ArgumentException("category length must be equal to or lower than 128!", nameof(category));
        }

        Category = string.IsNullOrWhiteSpace(normalizedCategory) ? null : normalizedCategory;
        Priority = priority;

        var normalizedActionUrl = actionUrl?.Trim();
        if (normalizedActionUrl is { Length: > 2048 })
        {
            throw new ArgumentException("actionUrl length must be equal to or lower than 2048!", nameof(actionUrl));
        }

        if (!string.IsNullOrWhiteSpace(normalizedActionUrl) &&
            (normalizedActionUrl[0] != '/' ||
             normalizedActionUrl.StartsWith("//", StringComparison.Ordinal) ||
             normalizedActionUrl.Contains('\\', StringComparison.Ordinal)))
        {
            throw new ArgumentException("actionUrl must be a safe application-relative path.", nameof(actionUrl));
        }

        ActionUrl = string.IsNullOrWhiteSpace(normalizedActionUrl) ? null : normalizedActionUrl;
        ScheduledForUtc = NormalizeScheduledForUtc(scheduledForUtc);
        var normalizedPreferenceUserId = preferenceUserId?.Trim();
        if (normalizedPreferenceUserId is { Length: > 128 })
        {
            throw new ArgumentException("preferenceUserId length must be equal to or lower than 128!", nameof(preferenceUserId));
        }

        PreferenceUserId = string.IsNullOrWhiteSpace(normalizedPreferenceUserId) ? null : normalizedPreferenceUserId;
    }

    public void AttachDispatchStep(Guid stepId, Guid scopeId)
    {
        if (stepId == Guid.Empty || TenantId == Guid.Empty || scopeId != (TenantId ?? Guid.Empty) || DispatchStepId.HasValue)
            throw new InvalidOperationException("A notification must attach once to a step in its exact tenant or host scope.");
        DispatchStepId = stepId;
        DispatchScopeId = scopeId;
    }

    private static DateTime? NormalizeScheduledForUtc(DateTime? value)
    {
        if (!value.HasValue)
        {
            return null;
        }

        if (value.Value.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Scheduled delivery time must be UTC.", nameof(value));
        }

        return value.Value;
    }
}
