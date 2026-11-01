using Notifications.Application.Contracts.Common;

namespace Notifications.Application.Contracts.Templates;

public class NotificationTemplateRevisionListItemDto
{
    public Guid Id { get; set; }
    public int RevisionNumber { get; set; }
    public string DisplayName { get; set; } = "";
    public string Category { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public Guid? CreatorId { get; set; }
    public bool IsPublished { get; set; }
}

public class NotificationTemplateManageDetailDto
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Category { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string SourceType { get; set; } = "Database";
    public bool SupportsPreview { get; set; } = true;
    public string? DraftSubjectTemplate { get; set; }
    public string DraftBodyTemplate { get; set; } = "";
    public int RevisionCount { get; set; }
    public int? PublishedRevisionNumber { get; set; }
    public DateTime? PublishedAtUtc { get; set; }
    public DateTime DraftUpdatedAtUtc { get; set; }
    public bool IsArchived { get; set; }
    public DateTime? ArchivedAtUtc { get; set; }
    public Guid? ArchivedByUserId { get; set; }
    public string? PublishedDisplayName { get; set; }
    public string? PublishedCategory { get; set; }
    public string? PublishedSubjectTemplate { get; set; }
    public string? PublishedBodyTemplate { get; set; }
    public List<NotificationTemplateRevisionListItemDto> Revisions { get; set; } = [];
}

public class CreateNotificationTemplateInput
{
    public string DisplayName { get; set; } = "";
    public string? Category { get; set; }
    public string? SubjectTemplate { get; set; }
    public string BodyTemplate { get; set; } = "";
}

public class SaveNotificationTemplateDraftInput
{
    public string TemplateName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Category { get; set; } = "";
    public string? SubjectTemplate { get; set; }
    public string BodyTemplate { get; set; } = "";
}

public class PreviewNotificationTemplateDraftInput
{
    public string TemplateName { get; set; } = "";
    public string? SubjectTemplate { get; set; }
    public string BodyTemplate { get; set; } = "";
    public Dictionary<string, string?> Model { get; set; } = new(StringComparer.Ordinal);
}

public class ValidateNotificationTemplateDraftInput
{
    public string TemplateName { get; set; } = "";
    public string? SubjectTemplate { get; set; }
    public string BodyTemplate { get; set; } = "";
    public Dictionary<string, string?> Model { get; set; } = new(StringComparer.Ordinal);
}

public class NotificationTemplateValidationResultDto
{
    public bool IsValid { get; set; }
    public bool CanRender { get; set; }
    public List<NotificationTemplateVariableDto> Variables { get; set; } = [];
    public List<string> MissingModelKeys { get; set; } = [];
    public Dictionary<string, string?> SuggestedModel { get; set; } = new(StringComparer.Ordinal);
    public List<NotificationTemplateValidationIssueDto> Issues { get; set; } = [];
}

public class NotificationTemplateVariableDto
{
    public string Source { get; set; } = "";
    public string Expression { get; set; } = "";
    public string ModelKey { get; set; } = "";
    public int Occurrences { get; set; }
}

public class NotificationTemplateValidationIssueDto
{
    public string Severity { get; set; } = "";
    public string Code { get; set; } = "";
    public string Source { get; set; } = "";
    public string Message { get; set; } = "";
}

public static class NotificationTemplateValidationSources
{
    public const string Subject = "Subject";
    public const string Body = "Body";
}

public static class NotificationTemplateValidationIssueSeverities
{
    public const string Error = "Error";
    public const string Warning = "Warning";
}

public static class NotificationTemplateValidationIssueCodes
{
    public const string BodyRequired = "BodyRequired";
    public const string TemplateSyntax = "TemplateSyntax";
    public const string MissingSampleValue = "MissingSampleValue";
}

public class PublishNotificationTemplateDraftInput
{
    public string TemplateName { get; set; } = "";
}

public class RepublishNotificationTemplateRevisionInput
{
    public string TemplateName { get; set; } = "";
    public Guid RevisionId { get; set; }
}

public class RenameNotificationTemplateInput
{
    public string TemplateName { get; set; } = "";
    public string NewDisplayName { get; set; } = "";
}

public class NotificationTemplateNameInput
{
    public string TemplateName { get; set; } = "";
}

public class NotificationTemplateSampleDto
{
    public Guid Id { get; set; }
    public string TemplateName { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public Dictionary<string, string?> Model { get; set; } = new(StringComparer.Ordinal);
    public bool IsDefault { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

public class CreateNotificationTemplateSampleInput
{
    public string TemplateName { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public Dictionary<string, string?> Model { get; set; } = new(StringComparer.Ordinal);
    public bool IsDefault { get; set; }
}

public class UpdateNotificationTemplateSampleInput
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public Dictionary<string, string?> Model { get; set; } = new(StringComparer.Ordinal);
    public bool IsDefault { get; set; }
}

public class GetNotificationTemplateContentBlocksInput : PagedRequestDto
{
    public string? Filter { get; set; }
    public string? Category { get; set; }
    public string? ArchiveState { get; set; }
}

public class NotificationTemplateContentBlockDto
{
    public Guid Id { get; set; }
    public string Key { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Category { get; set; } = "";
    public string? Description { get; set; }
    public string? SubjectSnippet { get; set; }
    public string? BodySnippet { get; set; }
    public bool IsArchived { get; set; }
    public DateTime? ArchivedAtUtc { get; set; }
    public Guid? ArchivedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

public class CreateNotificationTemplateContentBlockInput
{
    public string Key { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Category { get; set; } = "";
    public string? Description { get; set; }
    public string? SubjectSnippet { get; set; }
    public string? BodySnippet { get; set; }
}

public class UpdateNotificationTemplateContentBlockInput
{
    public Guid Id { get; set; }
    public string Key { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Category { get; set; } = "";
    public string? Description { get; set; }
    public string? SubjectSnippet { get; set; }
    public string? BodySnippet { get; set; }
}
