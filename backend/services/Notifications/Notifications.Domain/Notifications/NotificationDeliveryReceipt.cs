using Notifications.Domain.Shared.Enums;
using Shared.Domain;
using Shared.Domain.Entities;

namespace Notifications.Domain.Notifications;

/// <summary>
/// An append-only, provider-reported delivery-feedback fact for an email <see cref="NotificationDelivery"/>
/// (delivered / bounce / complaint). Records only safe, bounded metadata: no recipient address, raw
/// payload, email subject/body, or tracking URL is stored here. Idempotency is keyed by
/// (<see cref="ProviderName"/>, <see cref="ProviderEventId"/>).
/// </summary>
public class NotificationDeliveryReceipt : Entity<long>, IMultiTenant
{
    public Guid? TenantId { get; private set; }

    public long DeliveryId { get; private set; }

    /// <summary>The scoped provider configuration that produced the send, when known.</summary>
    public Guid? ProviderConfigId { get; private set; }

    /// <summary>The webhook provider name (route segment), e.g. the selected transactional-email provider.</summary>
    public string ProviderName { get; private set; } = default!;

    /// <summary>The provider's stable event id, used for idempotent processing.</summary>
    public string ProviderEventId { get; private set; } = default!;

    public EmailDeliveryEventType EventType { get; private set; }

    /// <summary>Bounded provider status/error code (e.g. "5.1.1"). Null when the provider omits it.</summary>
    public string? ProviderCode { get; private set; }

    /// <summary>Bounded, safe reason text. Never a raw payload or recipient value.</summary>
    public string? SafeReason { get; private set; }

    public DateTime OccurredAtUtc { get; private set; }

    public DateTime ReceivedAtUtc { get; private set; }

    private const int MaxProviderNameLength = 64;
    private const int MaxProviderEventIdLength = 256;
    private const int MaxProviderCodeLength = 64;
    private const int MaxSafeReasonLength = 512;

    protected NotificationDeliveryReceipt()
    {
    }

    public NotificationDeliveryReceipt(
        long id,
        Guid? tenantId,
        long deliveryId,
        Guid? providerConfigId,
        string providerName,
        string providerEventId,
        EmailDeliveryEventType eventType,
        string? providerCode,
        string? safeReason,
        DateTime occurredAtUtc,
        DateTime receivedAtUtc)
        : base(id)
    {
        TenantId = tenantId;
        DeliveryId = deliveryId;
        ProviderConfigId = providerConfigId;
        ProviderName = global::Shared.Check.NotNullOrWhiteSpace(providerName, nameof(providerName), maxLength: MaxProviderNameLength);
        ProviderEventId = global::Shared.Check.NotNullOrWhiteSpace(providerEventId, nameof(providerEventId), maxLength: MaxProviderEventIdLength);
        EventType = eventType;
        ProviderCode = Normalize(providerCode, MaxProviderCodeLength);
        SafeReason = Normalize(safeReason, MaxSafeReasonLength);
        OccurredAtUtc = NormalizeUtc(occurredAtUtc);
        ReceivedAtUtc = NormalizeUtc(receivedAtUtc);
    }

    private static string? Normalize(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
    }

    private static DateTime NormalizeUtc(DateTime value)
    {
        if (value == default)
        {
            return DateTime.UtcNow;
        }

        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }
}
