using Notifications.Domain.Shared.Constants;
using Notifications.Domain.Shared.Enums;

namespace Notifications.Application.Contracts.Replay;

public class ReplayNotificationInput
{
    public Guid NotificationId { get; set; }
    public Guid OperationId { get; set; }
    public DateTime? ScheduledForUtc { get; set; }
}

public class NotificationOverrideRecipientDto
{
    public NotificationRecipientType RecipientType { get; set; }
    public string RecipientValue { get; set; } = "";
}

public class NotificationReplayRecipientOptionDto
{
    public long RecipientId { get; set; }
    public NotificationRecipientType RecipientType { get; set; }
    public string DisplayLabel { get; set; } = "";
}

public class PreviewReplayNotificationOverrideInput
{
    public Guid NotificationId { get; set; }
    public List<long> RecipientIds { get; set; } = [];
}

public class ReplayNotificationOverrideInput
{
    public Guid NotificationId { get; set; }
    public Guid OperationId { get; set; }
    public List<long> RecipientIds { get; set; } = [];
    public DateTime? ScheduledForUtc { get; set; }
}

public class ReplayNotificationOverridePreviewDto
{
    public Guid NotificationId { get; set; }
    public Guid RequestId { get; set; }
    public Guid? TenantId { get; set; }
    public NotificationChannel Channel { get; set; }
    public string TemplateName { get; set; } = "";
    public string? Subject { get; set; }
    public int RecipientCount { get; set; }
    public List<NotificationReplayRecipientOptionDto> Recipients { get; set; } = [];
    public bool CanExecute { get; set; }
    public string? Warning { get; set; }
}

public class ReplayNotificationResultDto
{
    public bool Accepted { get; set; }
    public bool IsDuplicate { get; set; }
    public bool SkippedByPreferences { get; set; }
    public Guid RequestId { get; set; }
    public Guid NotificationId { get; set; }
    public Guid OriginalNotificationId { get; set; }
    public int QueuedCount { get; set; }
    public int RecipientCount { get; set; }
    public DateTime? ScheduledForUtc { get; set; }
}
