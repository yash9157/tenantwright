using System.Text.Json;
using Notifications.Domain.Shared.Enums;
using Shared.Domain;
using Shared.Domain.Entities.Auditing;

namespace Notifications.Domain.Policy;

public sealed class TenantNotificationPolicy : FullAuditedAggregateRoot<Guid>, IMultiTenant
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Guid? TenantId { get; private set; }
    public string IanaTimeZoneId { get; private set; } = "UTC";
    public bool QuietHoursEnabled { get; private set; }
    public int QuietStartLocalMinutes { get; private set; }
    public int QuietEndLocalMinutes { get; private set; }
    public NotificationPolicyChannel QuietChannels { get; private set; }
    public string BypassCategoriesJson { get; private set; } = "[]";
    public NotificationDigestFrequency DigestFrequency { get; private set; }
    public long Revision { get; private set; }

    private TenantNotificationPolicy()
    {
    }

    public TenantNotificationPolicy(Guid id, Guid tenantId) : base(id)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant ID is required.", nameof(tenantId));
        }

        TenantId = tenantId;
    }

    public void Update(
        string ianaTimeZoneId,
        bool quietHoursEnabled,
        int quietStartLocalMinutes,
        int quietEndLocalMinutes,
        NotificationPolicyChannel quietChannels,
        IEnumerable<string>? bypassCategories,
        NotificationDigestFrequency digestFrequency)
    {
        IanaTimeZoneId = NormalizeTimeZone(ianaTimeZoneId);
        ValidateMinutes(quietStartLocalMinutes, nameof(quietStartLocalMinutes));
        ValidateMinutes(quietEndLocalMinutes, nameof(quietEndLocalMinutes));
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

        if (!Enum.IsDefined(digestFrequency))
        {
            throw new ArgumentOutOfRangeException(nameof(digestFrequency));
        }

        QuietHoursEnabled = quietHoursEnabled;
        QuietStartLocalMinutes = quietStartLocalMinutes;
        QuietEndLocalMinutes = quietEndLocalMinutes;
        QuietChannels = quietChannels;
        BypassCategoriesJson = JsonSerializer.Serialize(NormalizeCategories(bypassCategories), JsonOptions);
        DigestFrequency = digestFrequency;
        Revision++;
    }

    public IReadOnlySet<string> GetBypassCategories()
    {
        var categories = JsonSerializer.Deserialize<List<string>>(BypassCategoriesJson, JsonOptions) ?? [];
        return categories.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    internal static string NormalizeTimeZone(string value)
    {
        return IanaTimeZoneRules.NormalizeAndValidate(value);
    }

    internal static void ValidateMinutes(int value, string parameterName)
    {
        if (value is < 0 or > 1439)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Local time must be expressed as minutes from 00:00 through 23:59.");
        }
    }

    private static List<string> NormalizeCategories(IEnumerable<string>? categories)
    {
        return (categories ?? [])
            .Select(category => category?.Trim())
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .Select(category => category!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(category => category, StringComparer.OrdinalIgnoreCase)
            .Take(64)
            .Select(category => category.Length <= 128 ? category : category[..128])
            .ToList();
    }
}
