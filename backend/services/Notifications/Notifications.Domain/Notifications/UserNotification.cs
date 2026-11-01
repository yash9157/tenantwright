using Shared.Domain;
using Shared.Domain.Entities.Auditing;
using Shared.AuditLogs.Auditing;

namespace Notifications.Domain.Notifications;

public class UserNotification : FullAuditedAggregateRoot<long>, IMultiTenant
{
    public Guid? TenantId { get; private set; }

    public Guid NotificationId { get; private set; }

    public virtual Notification Notification { get; private set; } = default!;

    public string UserId { get; private set; } = default!;

    public bool IsRead { get; private set; }

    public DateTime? ReadTimeUtc { get; private set; }

    public bool IsArchived { get; private set; }

    public DateTime? ArchivedTimeUtc { get; private set; }

    public long Revision { get; private set; } = 1;

    /// <summary>Rendered, sanitized inbox title (safe text, no markup). Null until rendered.</summary>
    public string? Title { get; private set; }

    /// <summary>Short single-line preview derived from the body (safe text). Null until rendered.</summary>
    public string? Summary { get; private set; }

    /// <summary>
    /// Rendered, structured safe-text body (block boundaries preserved as line breaks, no markup).
    /// Safe to render as text with a whitespace-preserving style. Null until rendered.
    /// </summary>
    public string? Body { get; private set; }

    protected UserNotification()
    {
    }

    public UserNotification(long id, Guid? tenantId, Guid notificationId, string userId)
        : base(id)
    {
        TenantId = tenantId;
        NotificationId = notificationId;
        UserId = global::Shared.Check.NotNullOrWhiteSpace(userId, nameof(userId), maxLength: 128);
        IsRead = false;
        IsArchived = false;
    }

    /// <summary>
    /// Sets the rendered inbox content. Values are trimmed/normalized only; no length capping is
    /// applied. Any value may be null (e.g. when rendering failed), in which case read-side
    /// fallbacks apply.
    /// </summary>
    public void SetRenderedContent(string? title, string? summary, string? body)
    {
        Title = Normalize(title);
        Summary = Normalize(summary);
        Body = string.IsNullOrWhiteSpace(body) ? null : body.Trim();
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public void MarkRead(DateTime utcNow)
    {
        if (IsRead)
        {
            return;
        }

        IsRead = true;
        ReadTimeUtc = utcNow;
        Revision++;
    }

    public void Archive(DateTime utcNow)
    {
        if (IsArchived)
        {
            return;
        }

        IsArchived = true;
        ArchivedTimeUtc = utcNow;

        // Archiving implies the notification has been seen.
        if (!IsRead)
        {
            IsRead = true;
            ReadTimeUtc = utcNow;
        }

        Revision++;
    }
}
