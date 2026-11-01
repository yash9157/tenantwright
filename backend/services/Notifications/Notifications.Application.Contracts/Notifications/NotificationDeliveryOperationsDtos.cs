using Notifications.Application.Contracts.Replay;
using Notifications.Domain.Shared.Constants;
using Notifications.Domain.Shared.Enums;

namespace Notifications.Application.Contracts.Notifications;

public class ResendNotificationDeliveryInput
{
    public long DeliveryId { get; set; }
}

public class ResendFailedNotificationDeliveriesInput
{
    public Guid NotificationId { get; set; }
}

public class ConfirmNotificationDeliveryInput
{
    public long DeliveryId { get; set; }
    public string? ProviderMessageId { get; set; }
}

public class RetryNotificationDeliveryInput
{
    public long DeliveryId { get; set; }
}

public class ReconcileNotificationDeliveryResultDto
{
    public long DeliveryId { get; set; }
    public NotificationStatus Status { get; set; }
    public Guid? DeliveryAttemptId { get; set; }
    public string? ProviderMessageId { get; set; }
}

public class PreviewNotificationDeliveryOverrideResendInput
{
    public long DeliveryId { get; set; }
    public List<NotificationOverrideRecipientDto> Recipients { get; set; } = [];
}

public class ResendNotificationDeliveryOverrideInput
{
    public long DeliveryId { get; set; }
    public List<NotificationOverrideRecipientDto> Recipients { get; set; } = [];
}

public class NotificationDeliveryOverridePreviewDto
{
    public long DeliveryId { get; set; }
    public Guid NotificationId { get; set; }
    public Guid? TenantId { get; set; }
    public NotificationChannel Channel { get; set; }
    public string TemplateName { get; set; } = "";
    public string? Subject { get; set; }
    public NotificationRecipientType OriginalRecipientType { get; set; }
    public string OriginalRecipientValue { get; set; } = "";
    public int RecipientCount { get; set; }
    public List<NotificationOverrideRecipientDto> Recipients { get; set; } = [];
    public bool CanExecute { get; set; }
    public string? Warning { get; set; }
}

public class ResendNotificationDeliveryResultDto
{
    public bool Accepted { get; set; }
    public bool IsDuplicate { get; set; }
    public bool SkippedByPreferences { get; set; }
    public Guid RequestId { get; set; }
    public Guid NotificationId { get; set; }
    public Guid OriginalNotificationId { get; set; }
    public int QueuedCount { get; set; }
    public int RecipientCount { get; set; }
    public int FailedDeliveryCount { get; set; }
}
