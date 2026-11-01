using Notifications.Application.Contracts.Common;
using Notifications.Domain.Shared.Enums;

namespace Notifications.Application.Contracts.UserNotifications;

public class GetUserNotificationsInput : PagedRequestDto
{
    public bool OnlyUnread { get; set; }

    /// <summary>Return only archived items (an "Archived" view). Takes precedence over IncludeArchived.</summary>
    public bool OnlyArchived { get; set; }

    /// <summary>Include archived items alongside active ones. Ignored when OnlyArchived is set. Default excludes archived.</summary>
    public bool IncludeArchived { get; set; }

    public string? Category { get; set; }

    public NotificationPriority? Priority { get; set; }
}

public class UserNotificationDto
{
    public Guid NotificationId { get; set; }
    public bool IsRead { get; set; }
    public DateTime? ReadTimeUtc { get; set; }
    public bool IsArchived { get; set; }
    public DateTime? ArchivedTimeUtc { get; set; }
    public DateTime CreationTime { get; set; }
    public string TemplateName { get; set; } = "";
    public string TemplateModelJson { get; set; } = "";

    /// <summary>Rendered, safe-text inbox title. Falls back to Subject/TemplateName when unrendered.</summary>
    public string? Title { get; set; }

    /// <summary>Short single-line preview derived from the body. Null when unrendered.</summary>
    public string? Summary { get; set; }

    /// <summary>Rendered, structured safe-text body (no markup). Null when unrendered.</summary>
    public string? Body { get; set; }

    public string? Subject { get; set; }
    public string? Category { get; set; }
    public NotificationPriority Priority { get; set; }
    public string? ActionUrl { get; set; }
    public long Revision { get; set; }
}

public class MarkUserNotificationReadInput
{
    public Guid NotificationId { get; set; }
    public long ExpectedRevision { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
}

public class ArchiveUserNotificationInput
{
    public Guid NotificationId { get; set; }
    public long ExpectedRevision { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
}

public class DeleteUserNotificationInput
{
    public Guid NotificationId { get; set; }
    public long ExpectedRevision { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
}

public sealed class MarkAllUserNotificationsReadInput
{
    public string IdempotencyKey { get; set; } = string.Empty;
}

public class MarkAllUserNotificationsReadResultDto
{
    public int UpdatedCount { get; set; }
    public NotificationOperationReceiptDto Receipt { get; set; } = new();
}

public interface IUserNotificationAppService
{
    Task<PagedResultDto<UserNotificationDto>> GetInboxAsync(GetUserNotificationsInput input);
    Task<UserNotificationDto> GetAsync(Guid notificationId);
    Task<NotificationMutationResultDto> MarkAsReadAsync(MarkUserNotificationReadInput input);
    Task<MarkAllUserNotificationsReadResultDto> MarkAllAsReadAsync(MarkAllUserNotificationsReadInput input);
    Task<NotificationMutationResultDto> ArchiveAsync(ArchiveUserNotificationInput input);
    Task<NotificationMutationResultDto> DeleteAsync(DeleteUserNotificationInput input);
}
