using Notifications.Domain.Shared.Enums;
using Shared.Domain;
using Shared.Domain.Entities.Auditing;

namespace Notifications.Domain.Digests;

public sealed class NotificationDigestBucket : FullAuditedAggregateRoot<Guid>, IMultiTenant
{
    public Guid? TenantId { get; private set; }
    public string ScopeKey { get; private set; } = "host";
    public NotificationChannel Channel { get; private set; }
    public NotificationRecipientType RecipientType { get; private set; }
    public string RecipientValue { get; private set; } = default!;
    public string RecipientValueHash { get; private set; } = default!;
    public NotificationDigestFrequency Frequency { get; private set; }
    public DateTime PeriodStartUtc { get; private set; }
    public DateTime PeriodEndUtc { get; private set; }
    public NotificationDigestBucketStatus Status { get; private set; }
    public int AttemptCount { get; private set; }
    public DateTime? EmittedAtUtc { get; private set; }
    public Guid? EmittedNotificationId { get; private set; }

    private NotificationDigestBucket()
    {
    }

    public NotificationDigestBucket(
        Guid id,
        Guid? tenantId,
        NotificationChannel channel,
        NotificationRecipientType recipientType,
        string recipientValue,
        NotificationDigestFrequency frequency,
        DateTime periodStartUtc,
        DateTime periodEndUtc) : base(id)
    {
        if (frequency == NotificationDigestFrequency.Disabled)
        {
            throw new ArgumentOutOfRangeException(nameof(frequency));
        }

        if (periodStartUtc.Kind != DateTimeKind.Utc || periodEndUtc.Kind != DateTimeKind.Utc || periodEndUtc <= periodStartUtc)
        {
            throw new ArgumentException("Digest period must be a valid UTC range.");
        }

        TenantId = tenantId;
        ScopeKey = tenantId?.ToString("D") ?? "host";
        Channel = channel;
        RecipientType = recipientType;
        RecipientValue = global::Shared.Check.NotNullOrWhiteSpace(recipientValue, nameof(recipientValue), maxLength: 4096);
        RecipientValueHash = global::Notifications.Domain.Notifications.NotificationDestinationFingerprint.Compute(RecipientValue);
        Frequency = frequency;
        PeriodStartUtc = periodStartUtc;
        PeriodEndUtc = periodEndUtc;
        Status = NotificationDigestBucketStatus.Open;
    }

    public void MarkProcessing()
    {
        if (Status is NotificationDigestBucketStatus.Emitted or NotificationDigestBucketStatus.ClosedEmpty)
        {
            return;
        }

        Status = NotificationDigestBucketStatus.Processing;
        AttemptCount++;
    }

    public void MarkEmitted(Guid notificationId, DateTime emittedAtUtc)
    {
        if (notificationId == Guid.Empty)
        {
            throw new ArgumentException("Emitted notification ID is required.", nameof(notificationId));
        }

        Status = NotificationDigestBucketStatus.Emitted;
        EmittedNotificationId = notificationId;
        EmittedAtUtc = emittedAtUtc.Kind == DateTimeKind.Utc
            ? emittedAtUtc
            : emittedAtUtc.ToUniversalTime();
    }

    public void MarkClosedEmpty(DateTime closedAtUtc)
    {
        Status = NotificationDigestBucketStatus.ClosedEmpty;
        EmittedNotificationId = null;
        EmittedAtUtc = closedAtUtc.Kind == DateTimeKind.Utc
            ? closedAtUtc
            : closedAtUtc.ToUniversalTime();
    }

    public void Reopen()
    {
        if (Status is not (NotificationDigestBucketStatus.Emitted or NotificationDigestBucketStatus.NeedsReview))
        {
            Status = NotificationDigestBucketStatus.Open;
        }
    }

    public void RequireReview() => Status = NotificationDigestBucketStatus.NeedsReview;
}
