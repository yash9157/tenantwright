using Notifications.Application.Contracts.Common;
using Notifications.Domain.Shared.Enums;

namespace Notifications.Application.Contracts.Policy;

public sealed class NotificationPolicyDto
{
    public Guid TenantId { get; set; }
    public string IanaTimeZoneId { get; set; } = "UTC";
    public bool QuietHoursEnabled { get; set; }
    public int QuietStartLocalMinutes { get; set; }
    public int QuietEndLocalMinutes { get; set; }
    public NotificationPolicyChannel QuietChannels { get; set; }
    public List<string> BypassCategories { get; set; } = [];
    public NotificationDigestFrequency DigestFrequency { get; set; }
    public DateTime? LastUpdatedAtUtc { get; set; }
    public long Revision { get; set; }
    public NotificationOperationReceiptDto? Receipt { get; set; }
}

public sealed class UpdateNotificationPolicyInput
{
    public string IanaTimeZoneId { get; set; } = "UTC";
    public bool QuietHoursEnabled { get; set; }
    public int QuietStartLocalMinutes { get; set; }
    public int QuietEndLocalMinutes { get; set; }
    public NotificationPolicyChannel QuietChannels { get; set; }
    public List<string> BypassCategories { get; set; } = [];
    public NotificationDigestFrequency DigestFrequency { get; set; }
    public long ExpectedRevision { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
}

public sealed class UserNotificationPreferenceDto
{
    public Guid? TenantId { get; set; }
    public bool QuietHoursEnabled { get; set; }
    public string IanaTimeZoneId { get; set; } = "UTC";
    public int QuietStartLocalMinutes { get; set; }
    public int QuietEndLocalMinutes { get; set; }
    public NotificationPolicyChannel QuietChannels { get; set; }
    public NotificationDigestFrequency? DigestFrequency { get; set; }
    public DateTime? LastUpdatedAtUtc { get; set; }
    public long Revision { get; set; }
    public NotificationOperationReceiptDto? Receipt { get; set; }
}

public sealed class UpdateUserNotificationPreferenceInput
{
    public bool QuietHoursEnabled { get; set; }
    public string IanaTimeZoneId { get; set; } = "UTC";
    public int QuietStartLocalMinutes { get; set; }
    public int QuietEndLocalMinutes { get; set; }
    public NotificationPolicyChannel QuietChannels { get; set; }
    public NotificationDigestFrequency? DigestFrequency { get; set; }
    public long ExpectedRevision { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
}

public sealed class NotificationPolicyPreviewInput
{
    public Guid TenantId { get; set; }
    public NotificationChannel Channel { get; set; }
    public string? Category { get; set; }
    public NotificationPriority Priority { get; set; } = NotificationPriority.Normal;
    public DateTime AtUtc { get; set; }
}

public sealed class NotificationPolicyPreviewDto
{
    public bool WillBeHeld { get; set; }
    public bool Bypassed { get; set; }
    public string Reason { get; set; } = "";
    public DateTime? NextAllowedAtUtc { get; set; }
}

public interface INotificationPolicyAppService
{
    Task<NotificationPolicyDto> GetTenantPolicyAsync(Guid tenantId, CancellationToken cancellationToken = default);
    Task<NotificationPolicyDto> UpdateTenantPolicyAsync(Guid tenantId, UpdateNotificationPolicyInput input, CancellationToken cancellationToken = default);
    Task<NotificationPolicyPreviewDto> PreviewTenantPolicyAsync(NotificationPolicyPreviewInput input, CancellationToken cancellationToken = default);
    Task<UserNotificationPreferenceDto> GetMyPreferenceAsync(CancellationToken cancellationToken = default);
    Task<UserNotificationPreferenceDto> UpdateMyPreferenceAsync(UpdateUserNotificationPreferenceInput input, CancellationToken cancellationToken = default);
}
