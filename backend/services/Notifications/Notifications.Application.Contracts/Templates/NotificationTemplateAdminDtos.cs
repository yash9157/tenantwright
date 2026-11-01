using Notifications.Application.Contracts.Common;

namespace Notifications.Application.Contracts.Templates;

public class GetNotificationTemplatesInput : PagedRequestDto
{
    public string? Filter { get; set; }
    public string? ArchiveState { get; set; }
}

public static class NotificationTemplateArchiveStates
{
    public const string Active = "active";
    public const string Archived = "archived";
    public const string All = "all";
}

public class NotificationTemplateListItemDto
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Category { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string SourceType { get; set; } = "FileSystem";
    public bool SupportsPreview { get; set; } = true;
    public int RevisionCount { get; set; }
    public int? PublishedRevisionNumber { get; set; }
    public DateTime? PublishedAtUtc { get; set; }
    public bool IsArchived { get; set; }
    public DateTime? ArchivedAtUtc { get; set; }
    public Guid? ArchivedByUserId { get; set; }
}

public class NotificationTemplateDetailDto
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Category { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string SourceType { get; set; } = "FileSystem";
    public bool SupportsPreview { get; set; } = true;
    public bool IsArchived { get; set; }
    public DateTime? ArchivedAtUtc { get; set; }
    public Guid? ArchivedByUserId { get; set; }
}
