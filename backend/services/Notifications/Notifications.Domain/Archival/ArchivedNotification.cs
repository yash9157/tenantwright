using Shared.Domain;
using Shared.Domain.Entities;

namespace Notifications.Domain.Archival;

/// <summary>
/// Cold archive envelope created before the hot notification aggregate is removed. The payload is
/// service-owned and intentionally has no read/restore API in this gate.
/// </summary>
public sealed class ArchivedNotification : Entity<Guid>, IMultiTenant
{
    public Guid? TenantId { get; private set; }
    public Guid SourceNotificationId { get; private set; }
    public DateTime SourceCreatedAtUtc { get; private set; }
    public DateTime ArchivedAtUtc { get; private set; }
    public string PayloadJson { get; private set; } = default!;
    public string PayloadSha256 { get; private set; } = default!;

    private ArchivedNotification()
    {
    }

    public ArchivedNotification(
        Guid id,
        Guid? tenantId,
        Guid sourceNotificationId,
        DateTime sourceCreatedAtUtc,
        DateTime archivedAtUtc,
        string payloadJson,
        string payloadSha256) : base(id)
    {
        TenantId = tenantId;
        SourceNotificationId = sourceNotificationId;
        SourceCreatedAtUtc = sourceCreatedAtUtc;
        ArchivedAtUtc = archivedAtUtc;
        PayloadJson = global::Shared.Check.NotNullOrWhiteSpace(payloadJson, nameof(payloadJson));
        PayloadSha256 = global::Shared.Check.NotNullOrWhiteSpace(payloadSha256, nameof(payloadSha256), maxLength: 64);
    }
}
