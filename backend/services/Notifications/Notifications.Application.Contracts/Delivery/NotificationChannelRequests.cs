using Shared.Domain;
using Shared.EventContracts;

namespace Notifications.Application.Contracts.Delivery;

public sealed class NotificationChannelRequestTenant : IMultiTenant
{
    public Guid? TenantId { get; set; }
    public Guid RequestId { get; set; }
    public Guid? DispatchStepId { get; set; }
    public Guid? SchedulingClaimId { get; set; }
    public Guid? DigestBucketId { get; set; }
    public NotificationSendChannel Channel { get; set; } = NotificationSendChannel.Email;
    public string? Subject { get; set; }
    public string TemplateName { get; set; } = string.Empty;
    public Dictionary<string, string?> TemplateModel { get; set; } = new(StringComparer.Ordinal);
    public List<NotificationRecipientEto> Recipients { get; set; } = [];
    public string? Category { get; set; }
    public NotificationSendPriority Priority { get; set; } = NotificationSendPriority.Normal;
    public string? ActionUrl { get; set; }
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ScheduledForUtc { get; set; }
    public string SourceService { get; set; } = string.Empty;
    public bool RespectUserPreferences { get; set; }
    public Guid? PreferenceUserId { get; set; }
    public string? PreferenceKey { get; set; }
}

public sealed class NotificationChannelRequestHost
{
    public Guid RequestId { get; set; }
    public Guid? DispatchStepId { get; set; }
    public Guid? SchedulingClaimId { get; set; }
    public Guid? DigestBucketId { get; set; }
    public NotificationSendChannel Channel { get; set; } = NotificationSendChannel.Email;
    public string? Subject { get; set; }
    public string TemplateName { get; set; } = string.Empty;
    public Dictionary<string, string?> TemplateModel { get; set; } = new(StringComparer.Ordinal);
    public List<NotificationRecipientEto> Recipients { get; set; } = [];
    public string? Category { get; set; }
    public NotificationSendPriority Priority { get; set; } = NotificationSendPriority.Normal;
    public string? ActionUrl { get; set; }
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ScheduledForUtc { get; set; }
    public string SourceService { get; set; } = string.Empty;
    public bool RespectUserPreferences { get; set; }
    public Guid? PreferenceUserId { get; set; }
    public string? PreferenceKey { get; set; }
}
