using Shared.Domain;
using Shared.Domain.Entities;

namespace Notifications.Domain.Digests;

public sealed class NotificationDigestItem : Entity<long>, IMultiTenant
{
    public Guid? TenantId { get; private set; }
    public Guid BucketId { get; private set; }
    public Guid SourceNotificationId { get; private set; }
    public string TemplateName { get; private set; } = default!;
    public string? Category { get; private set; }
    public DateTime AddedAtUtc { get; private set; }

    private NotificationDigestItem()
    {
    }

    public NotificationDigestItem(
        long id,
        Guid? tenantId,
        Guid bucketId,
        Guid sourceNotificationId,
        string templateName,
        string? category,
        DateTime addedAtUtc) : base(id)
    {
        TenantId = tenantId;
        BucketId = bucketId;
        SourceNotificationId = sourceNotificationId;
        TemplateName = global::Shared.Check.NotNullOrWhiteSpace(templateName, nameof(templateName), maxLength: 256);
        var normalizedCategory = category?.Trim();
        Category = string.IsNullOrWhiteSpace(normalizedCategory)
            ? null
            : normalizedCategory.Length <= 128 ? normalizedCategory : normalizedCategory[..128];
        AddedAtUtc = addedAtUtc.Kind == DateTimeKind.Utc ? addedAtUtc : addedAtUtc.ToUniversalTime();
    }
}
