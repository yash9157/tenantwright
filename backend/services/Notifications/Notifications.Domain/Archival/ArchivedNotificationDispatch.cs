using Shared.Domain;
using Shared.Domain.Entities;

namespace Notifications.Domain.Archival;

/// <summary>A redacted dispatch header or one immutable history entry, archived before its hot row is removed.</summary>
public sealed class ArchivedNotificationDispatch : Entity<Guid>, IMultiTenant
{
    public Guid? TenantId { get; private set; }
    public Guid ScopeId { get; private set; }
    public Guid SourceDispatchId { get; private set; }
    // The reserved empty identity denotes the header. Domain events always have a nonempty UUID.
    public Guid SourceEventId { get; private set; }
    public DateTime ArchivedAtUtc { get; private set; }
    public string PayloadJson { get; private set; } = string.Empty;
    public string PayloadSha256 { get; private set; } = string.Empty;

    private ArchivedNotificationDispatch() { }

    public ArchivedNotificationDispatch(Guid id, Guid? tenantId, Guid sourceDispatchId, Guid sourceEventId,
        DateTime archivedAtUtc, string payloadJson, string payloadSha256) : base(id)
    {
        if (id == Guid.Empty || tenantId == Guid.Empty || sourceDispatchId == Guid.Empty || archivedAtUtc.Kind != DateTimeKind.Utc ||
            payloadSha256.Length != 64 || !payloadSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("Invalid notification dispatch archive identity, time or checksum.");
        TenantId = tenantId;
        ScopeId = tenantId ?? Guid.Empty;
        SourceDispatchId = sourceDispatchId;
        SourceEventId = sourceEventId;
        ArchivedAtUtc = archivedAtUtc;
        PayloadJson = global::Shared.Check.NotNullOrWhiteSpace(payloadJson, nameof(payloadJson), maxLength: 262144);
        PayloadSha256 = payloadSha256;
    }
}
