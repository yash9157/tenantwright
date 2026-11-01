using Notifications.Domain.Shared.Enums;
using Shared.Domain;
using Shared.Domain.Entities;

namespace Notifications.Domain.Notifications;

/// <summary>
/// Append-only, tenant-owned delivery chronology. It intentionally stores no recipient value,
/// template payload, provider secret, or raw provider response.
/// </summary>
public sealed class NotificationDeliveryChronology : Entity<long>, IMultiTenant
{
    public const int MaxProviderMessageIdLength = 256;
    public const int MaxResponseCodeLength = 128;
    public const int MaxSafeErrorLength = 512;
    public const int MaxActorIdLength = 128;

    public Guid? TenantId { get; private set; }
    public long DeliveryId { get; private set; }
    public NotificationDelivery Delivery { get; private set; } = default!;
    public NotificationDeliveryEventKind EventKind { get; private set; }
    public NotificationStatus Status { get; private set; }
    public string? ProviderMessageId { get; private set; }
    public string? ResponseCode { get; private set; }
    public string? SafeError { get; private set; }
    public long? LatencyMs { get; private set; }
    public string? ActorId { get; private set; }
    public NotificationDeliveryActorType ActorType { get; private set; }
    public DateTime OccurredAtUtc { get; private set; }

    private NotificationDeliveryChronology()
    {
    }

    internal NotificationDeliveryChronology(
        NotificationDelivery delivery,
        NotificationDeliveryEventKind eventKind,
        NotificationStatus status,
        DateTime occurredAtUtc,
        string? providerMessageId = null,
        string? responseCode = null,
        string? safeError = null,
        long? latencyMs = null,
        string? actorId = null,
        NotificationDeliveryActorType actorType = NotificationDeliveryActorType.System)
    {
        Delivery = delivery ?? throw new ArgumentNullException(nameof(delivery));
        TenantId = delivery.TenantId;
        DeliveryId = delivery.Id;
        EventKind = eventKind;
        Status = status;
        ProviderMessageId = Normalize(providerMessageId, MaxProviderMessageIdLength);
        ResponseCode = Normalize(responseCode, MaxResponseCodeLength);
        SafeError = Normalize(safeError, MaxSafeErrorLength);
        LatencyMs = latencyMs is < 0 ? 0 : latencyMs;
        ActorId = Normalize(actorId, MaxActorIdLength);
        ActorType = actorType;
        OccurredAtUtc = NormalizeUtc(occurredAtUtc);
    }

    private static string? Normalize(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
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
