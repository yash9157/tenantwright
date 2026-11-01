using Notifications.Domain.Shared.Constants;
using Notifications.Domain.Shared.Enums;
using Shared.Domain;
using Shared.Domain.Entities.Auditing;

namespace Notifications.Domain.Notifications;

public class NotificationDelivery : FullAuditedAggregateRoot<long>, IMultiTenant
{
    private readonly List<NotificationDeliveryChronology> _chronology = [];

    public Guid? TenantId { get; private set; }
    public Guid NotificationId { get; private set; }
    public virtual Notification Notification { get; private set; } = default!;
    public long RecipientId { get; private set; }
    public virtual NotificationRecipient Recipient { get; private set; } = default!;
    public NotificationStatus Status { get; private set; }
    public string? ProviderMessageId { get; private set; }

    /// <summary>
    /// Which scoped NotificationProviderConfig produced this send, when the channel resolves a
    /// provider configuration. Null for channels with no scoped configuration and legacy rows.
    /// </summary>
    public Guid? ProviderConfigId { get; private set; }

    public string? LastError { get; private set; }
    public int TryCount { get; private set; }
    public DateTime? LastTryTimeUtc { get; private set; }
    public DateTime? SentTimeUtc { get; private set; }
    public DateTime? NextAttemptTimeUtc { get; private set; }
    public DateTime? NotBeforeUtc { get; private set; }
    public DateTime? PolicyHeldUntilUtc { get; private set; }
    public Guid? DeliveryAttemptId { get; private set; }
    public DateTime? DeliveryAttemptedAtUtc { get; private set; }
    public virtual IReadOnlyCollection<NotificationDeliveryChronology> Chronology => _chronology;

    protected NotificationDelivery()
    {
    }

    public NotificationDelivery(
        long id,
        Guid? tenantId,
        Guid notificationId,
        long recipientId,
        DateTime? notBeforeUtc = null)
        : base(id)
    {
        TenantId = tenantId;
        NotificationId = notificationId;
        RecipientId = recipientId;
        Status = NotificationStatus.Queued;
        TryCount = 0;
        NextAttemptTimeUtc = notBeforeUtc ?? DateTime.UtcNow;
        NotBeforeUtc = notBeforeUtc;
        AppendChronology(NotificationDeliveryEventKind.Queued, Status, DateTime.UtcNow);
    }

    public void HoldForQuietHours(DateTime nextAllowedAtUtc, DateTime occurredAtUtc)
    {
        if (nextAllowedAtUtc.Kind != DateTimeKind.Utc || nextAllowedAtUtc <= occurredAtUtc)
        {
            throw new ArgumentException("Quiet-hours release time must be a future UTC value.", nameof(nextAllowedAtUtc));
        }

        Status = NotificationStatus.Queued;
        NextAttemptTimeUtc = nextAllowedAtUtc;
        PolicyHeldUntilUtc = nextAllowedAtUtc;
        AppendChronology(
            NotificationDeliveryEventKind.QuietHoursHeld,
            Status,
            occurredAtUtc,
            actorId: "notification-policy");
    }

    public void ReleaseQuietHoursHold(DateTime occurredAtUtc)
    {
        if (!PolicyHeldUntilUtc.HasValue)
        {
            return;
        }

        PolicyHeldUntilUtc = null;
        AppendChronology(
            NotificationDeliveryEventKind.QuietHoursReleased,
            Status,
            occurredAtUtc,
            actorId: "notification-policy");
    }

    public void MarkProcessing(DateTime utcNow, DateTime claimExpiresUtc)
    {
        MarkProcessing(utcNow, claimExpiresUtc, Guid.CreateVersion7());
    }

    public void MarkProcessing(DateTime utcNow, DateTime claimExpiresUtc, Guid deliveryAttemptId)
    {
        if (deliveryAttemptId == Guid.Empty)
        {
            throw new ArgumentException("Delivery attempt ID is required.", nameof(deliveryAttemptId));
        }

        Status = NotificationStatus.Processing;
        TryCount++;
        LastTryTimeUtc = utcNow;
        NextAttemptTimeUtc = claimExpiresUtc;
        DeliveryAttemptId = deliveryAttemptId;
        DeliveryAttemptedAtUtc = utcNow;
        AppendChronology(NotificationDeliveryEventKind.Processing, Status, utcNow);
    }

    public void MarkProviderAccepted(
        DateTime utcNow,
        string? providerMessageId,
        Guid? providerConfigId = null,
        string? actorId = null,
        NotificationDeliveryActorType actorType = NotificationDeliveryActorType.System,
        NotificationDeliveryEventKind eventKind = NotificationDeliveryEventKind.ProviderAccepted,
        string? responseCode = null,
        long? latencyMs = null)
    {
        Status = NotificationStatus.ProviderAccepted;
        SentTimeUtc = utcNow;
        var normalizedProviderMessageId = providerMessageId?.Trim();
        if (normalizedProviderMessageId is { Length: > NotificationDeliveryChronology.MaxProviderMessageIdLength })
        {
            throw new ArgumentException(
                $"providerMessageId length must be equal to or lower than {NotificationDeliveryChronology.MaxProviderMessageIdLength}!",
                nameof(providerMessageId));
        }

        ProviderMessageId = string.IsNullOrWhiteSpace(normalizedProviderMessageId) ? null : normalizedProviderMessageId;
        ProviderConfigId = providerConfigId;
        LastError = null;
        NextAttemptTimeUtc = null;
        AppendChronology(
            eventKind,
            Status,
            utcNow,
            ProviderMessageId,
            responseCode,
            latencyMs: latencyMs ?? CalculateLatencyMs(utcNow),
            actorId: actorId,
            actorType: actorType);
    }

    public void MarkDelivered(
        DateTime utcNow,
        string? providerMessageId = null,
        Guid? providerConfigId = null,
        string? actorId = null,
        NotificationDeliveryActorType actorType = NotificationDeliveryActorType.System,
        string? responseCode = null,
        NotificationDeliveryEventKind eventKind = NotificationDeliveryEventKind.Delivered)
    {
        if (Status is not (NotificationStatus.Processing or NotificationStatus.ProviderAccepted))
        {
            throw new InvalidOperationException("Only processing or provider-accepted delivery can be confirmed delivered.");
        }

        var normalizedProviderMessageId = (providerMessageId ?? ProviderMessageId)?.Trim();
        if (normalizedProviderMessageId is { Length: > NotificationDeliveryChronology.MaxProviderMessageIdLength })
        {
            throw new ArgumentException(
                $"providerMessageId length must be equal to or lower than {NotificationDeliveryChronology.MaxProviderMessageIdLength}!",
                nameof(providerMessageId));
        }

        Status = NotificationStatus.Delivered;
        SentTimeUtc = utcNow;
        ProviderMessageId = string.IsNullOrWhiteSpace(normalizedProviderMessageId) ? null : normalizedProviderMessageId;
        ProviderConfigId = providerConfigId ?? ProviderConfigId;
        LastError = null;
        NextAttemptTimeUtc = null;
        AppendChronology(
            eventKind,
            Status,
            utcNow,
            ProviderMessageId,
            responseCode,
            latencyMs: CalculateLatencyMs(utcNow),
            actorId: actorId,
            actorType: actorType);
    }

    public void MarkFailed(
        string error,
        DateTime nextAttemptTimeUtc,
        DateTime? occurredAtUtc = null,
        string? responseCode = null)
    {
        Status = NotificationStatus.Failed;
        LastError = global::Shared.Check.NotNullOrWhiteSpace(error, nameof(error), maxLength: 2048);
        NextAttemptTimeUtc = nextAttemptTimeUtc;
        var eventTime = occurredAtUtc ?? DateTime.UtcNow;
        AppendChronology(
            NotificationDeliveryEventKind.Failed,
            Status,
            eventTime,
            ProviderMessageId,
            responseCode,
            LastError,
            CalculateLatencyMs(eventTime));
    }

    public void MarkNeedsReview(
        string error,
        DateTime utcNow,
        string? actorId = null,
        NotificationDeliveryActorType actorType = NotificationDeliveryActorType.System)
    {
        Status = NotificationStatus.NeedsReview;
        LastError = global::Shared.Check.NotNullOrWhiteSpace(error, nameof(error), maxLength: 2048);
        LastTryTimeUtc = utcNow;
        NextAttemptTimeUtc = null;
        AppendChronology(
            NotificationDeliveryEventKind.NeedsReview,
            Status,
            utcNow,
            ProviderMessageId,
            safeError: LastError,
            latencyMs: CalculateLatencyMs(utcNow),
            actorId: actorId,
            actorType: actorType);
    }

    public void MarkDeadLettered(
        string error,
        DateTime utcNow,
        string? actorId = null,
        NotificationDeliveryActorType actorType = NotificationDeliveryActorType.System,
        string? responseCode = null)
    {
        Status = NotificationStatus.DeadLettered;
        LastError = global::Shared.Check.NotNullOrWhiteSpace(error, nameof(error), maxLength: 2048);
        LastTryTimeUtc = utcNow;
        NextAttemptTimeUtc = null;
        AppendChronology(
            NotificationDeliveryEventKind.DeadLettered,
            Status,
            utcNow,
            ProviderMessageId,
            responseCode,
            LastError,
            CalculateLatencyMs(utcNow),
            actorId,
            actorType);
    }

    public void MarkQueuedForOperatorRetry(DateTime utcNow, string? actorId = null)
    {
        if (Status != NotificationStatus.NeedsReview && Status != NotificationStatus.DeadLettered)
        {
            throw new InvalidOperationException("Only review-required or dead-lettered deliveries can be explicitly retried.");
        }

        Status = NotificationStatus.Queued;
        LastError = null;
        NextAttemptTimeUtc = utcNow;
        AppendChronology(
            NotificationDeliveryEventKind.Requeued,
            Status,
            utcNow,
            ProviderMessageId,
            actorId: actorId,
            actorType: NotificationDeliveryActorType.Operator);
    }

    public void RecordResendRequested(DateTime utcNow, string? actorId, bool recipientOverride)
    {
        AppendChronology(
            recipientOverride
                ? NotificationDeliveryEventKind.OverrideResendRequested
                : NotificationDeliveryEventKind.ResendRequested,
            Status,
            utcNow,
            ProviderMessageId,
            actorId: actorId,
            actorType: NotificationDeliveryActorType.Operator);
    }

    public void RecordProviderFeedback(
        DateTime utcNow,
        string providerName,
        string? responseCode,
        string? safeError,
        NotificationDeliveryEventKind eventKind = NotificationDeliveryEventKind.ProviderFeedback)
    {
        if (eventKind is not (NotificationDeliveryEventKind.ProviderFeedback or NotificationDeliveryEventKind.ProviderDelivered))
        {
            throw new ArgumentOutOfRangeException(nameof(eventKind));
        }

        AppendChronology(
            eventKind,
            Status,
            utcNow,
            ProviderMessageId,
            responseCode,
            safeError,
            actorId: providerName,
            actorType: NotificationDeliveryActorType.Provider);
    }

    private void AppendChronology(
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
        _chronology.Add(new NotificationDeliveryChronology(
            this,
            eventKind,
            status,
            occurredAtUtc,
            providerMessageId,
            responseCode,
            safeError,
            latencyMs,
            actorId,
            actorType));
    }

    private long? CalculateLatencyMs(DateTime completedAtUtc)
    {
        if (!DeliveryAttemptedAtUtc.HasValue)
        {
            return null;
        }

        return Math.Max(0, (long)(completedAtUtc - DeliveryAttemptedAtUtc.Value).TotalMilliseconds);
    }
}
