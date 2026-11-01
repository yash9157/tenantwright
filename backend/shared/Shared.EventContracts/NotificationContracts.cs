using Shared.Domain;
using Shared.Events;

namespace Shared.EventContracts;

[EventName("Shared.EventContracts.NotificationSendRequestedTenantEto")]
[DistributedContract(1, DistributedContractScope.TenantRequired, DistributedContractDataClassification.Sensitive)]
public sealed class NotificationSendRequestedTenantEto : IMultiTenant
{
    public Guid? TenantId { get; set; }
    public Guid RequestId { get; set; }
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

[EventName("Shared.EventContracts.NotificationSendRequestedHostEto")]
[DistributedContract(1, DistributedContractScope.HostOnly, DistributedContractDataClassification.Sensitive)]
public sealed class NotificationSendRequestedHostEto
{
    public Guid RequestId { get; set; }
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

public sealed class NotificationRecipientEto
{
    public NotificationSendRecipientType RecipientType { get; set; } = NotificationSendRecipientType.EmailAddress;
    public string RecipientValue { get; set; } = string.Empty;
}

public enum NotificationSendChannel
{
    Email = 1,
    Sms = 2,
    Push = 3,
    App = 4,
    WhatsApp = 5
}

public enum NotificationSendRecipientType
{
    EmailAddress = 1,
    PhoneNumber = 2,
    UserId = 3,
    DeviceToken = 4,
    Role = 5,
    AllTenantUsers = 8
}

public enum NotificationSendPriority
{
    Low = 1,
    Normal = 2,
    High = 3,
    Critical = 4
}
