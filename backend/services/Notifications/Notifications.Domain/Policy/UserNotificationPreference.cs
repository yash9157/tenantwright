using Notifications.Domain.Shared.Enums;
using Shared.Domain;
using Shared.Domain.Entities.Auditing;

namespace Notifications.Domain.Policy;

public sealed class UserNotificationPreference : FullAuditedAggregateRoot<Guid>, IMultiTenant
{
    public Guid? TenantId { get; private set; }
    public string ScopeKey { get; private set; } = "host";
    public string UserId { get; private set; } = default!;
    public bool QuietHoursEnabled { get; private set; }
    public string IanaTimeZoneId { get; private set; } = "UTC";
    public int QuietStartLocalMinutes { get; private set; }
    public int QuietEndLocalMinutes { get; private set; }
    public NotificationPolicyChannel QuietChannels { get; private set; }
    public NotificationDigestFrequency? DigestFrequency { get; private set; }
    public long Revision { get; private set; }

    private UserNotificationPreference()
    {
    }

    public UserNotificationPreference(Guid id, Guid? tenantId, string userId) : base(id)
    {
        TenantId = tenantId;
        ScopeKey = tenantId?.ToString("D") ?? "host";
        UserId = global::Shared.Check.NotNullOrWhiteSpace(userId, nameof(userId), maxLength: 128);
    }

    public void Update(
        bool quietHoursEnabled,
        string ianaTimeZoneId,
        int quietStartLocalMinutes,
        int quietEndLocalMinutes,
        NotificationPolicyChannel quietChannels,
        NotificationDigestFrequency? digestFrequency)
    {
        IanaTimeZoneId = TenantNotificationPolicy.NormalizeTimeZone(ianaTimeZoneId);
        TenantNotificationPolicy.ValidateMinutes(quietStartLocalMinutes, nameof(quietStartLocalMinutes));
        TenantNotificationPolicy.ValidateMinutes(quietEndLocalMinutes, nameof(quietEndLocalMinutes));
        if (quietHoursEnabled && quietStartLocalMinutes == quietEndLocalMinutes)
        {
            throw new ArgumentException("Quiet-hours start and end must differ.");
        }

        var supportedChannels = NotificationPolicyChannel.Email |
                                NotificationPolicyChannel.Sms |
                                NotificationPolicyChannel.Push;
        if ((quietChannels & ~supportedChannels) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quietChannels));
        }

        if (digestFrequency.HasValue && !Enum.IsDefined(digestFrequency.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(digestFrequency));
        }

        QuietHoursEnabled = quietHoursEnabled;
        QuietStartLocalMinutes = quietStartLocalMinutes;
        QuietEndLocalMinutes = quietEndLocalMinutes;
        QuietChannels = quietChannels;
        DigestFrequency = digestFrequency;
        Revision++;
    }
}
