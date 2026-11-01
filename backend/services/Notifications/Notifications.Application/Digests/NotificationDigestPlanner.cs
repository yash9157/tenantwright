using Microsoft.EntityFrameworkCore;
using Notifications.Domain.Digests;
using Notifications.Domain.Notifications;
using Notifications.Domain.Policy;
using Notifications.Domain.Shared.Enums;
using Notifications.EntityFrameworkCore.EntityFrameworkCore;
using Shared.Modularity.DependencyInjection;
using Shared.EntityFrameworkCore;
namespace Notifications.Application.Digests;

public interface INotificationDigestPlanner
{
    Task<bool> TryAddAsync(
        Notification notification,
        NotificationRecipient recipient,
        CancellationToken cancellationToken);
}

public sealed class NotificationDigestPlanner : INotificationDigestPlanner, ITransientDependency
{
    private readonly IDbContextProvider<NotificationsDbContextApp> _dbContextProvider;

    public NotificationDigestPlanner(IDbContextProvider<NotificationsDbContextApp> dbContextProvider)
    {
        _dbContextProvider = dbContextProvider;
    }

    public async Task<bool> TryAddAsync(
        Notification notification,
        NotificationRecipient recipient,
        CancellationToken cancellationToken)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        if (notification.Channel is NotificationChannel.App or NotificationChannel.WhatsApp ||
                    notification.Priority is NotificationPriority.High or NotificationPriority.Critical ||
                    IsMandatoryBypass(notification.Category))
        {
            return false;
        }

        TenantNotificationPolicy? policy = null;
        if (notification.TenantId.HasValue)
        {
            policy = await unitOfWorkDbContext.TenantNotificationPolicies
                .IgnoreQueryFilters()
                .SingleOrDefaultAsync(x => x.TenantId == notification.TenantId.Value, cancellationToken);
        }

        var userId = notification.PreferenceUserId;
        if (string.IsNullOrWhiteSpace(userId) && recipient.RecipientType == NotificationRecipientType.UserId)
        {
            userId = recipient.RecipientValue;
        }

        UserNotificationPreference? preference = null;
        if (!string.IsNullOrWhiteSpace(userId))
        {
            preference = await unitOfWorkDbContext.UserNotificationPreferences
                .IgnoreQueryFilters()
                .SingleOrDefaultAsync(x => x.TenantId == notification.TenantId && x.UserId == userId, cancellationToken);
        }

        var tenantFrequency = policy?.DigestFrequency ?? NotificationDigestFrequency.Disabled;
        var frequency = notification.TenantId.HasValue
            ? ResolveTenantFrequency(tenantFrequency, preference?.DigestFrequency)
            : preference?.DigestFrequency ?? NotificationDigestFrequency.Disabled;
        if (frequency == NotificationDigestFrequency.Disabled)
        {
            return false;
        }

        var timeZoneId = preference?.IanaTimeZoneId ?? policy?.IanaTimeZoneId ?? "UTC";
        var period = CalculatePeriod(DateTime.UtcNow, frequency, timeZoneId);
        var bucket = await unitOfWorkDbContext.NotificationDigestBuckets
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(
                x => x.TenantId == notification.TenantId &&
                     x.Channel == notification.Channel &&
                     x.RecipientType == recipient.RecipientType &&
                     x.RecipientValue == recipient.RecipientValue &&
                     x.Frequency == frequency &&
                     x.PeriodStartUtc == period.StartUtc,
                cancellationToken);
        if (bucket == null)
        {
            bucket = new NotificationDigestBucket(
                Guid.CreateVersion7(),
                notification.TenantId,
                notification.Channel,
                recipient.RecipientType,
                recipient.RecipientValue,
                frequency,
                period.StartUtc,
                period.EndUtc);
            unitOfWorkDbContext.NotificationDigestBuckets.Add(bucket);
        }

        var exists = await unitOfWorkDbContext.NotificationDigestItems
            .IgnoreQueryFilters()
            .AnyAsync(
                x => x.BucketId == bucket.Id && x.SourceNotificationId == notification.Id,
                cancellationToken);
        if (!exists)
        {
            unitOfWorkDbContext.NotificationDigestItems.Add(new NotificationDigestItem(
                0,
                notification.TenantId,
                bucket.Id,
                notification.Id,
                notification.TemplateName,
                notification.Category,
                DateTime.UtcNow));
        }

        // This context is plain constructor-injected, not repository-vended, so it is never
        // registered with the caller's ambient UnitOfWork and must be saved explicitly here.
        await unitOfWorkDbContext.SaveChangesAsync(cancellationToken);

        return true;
    }

    private static NotificationDigestFrequency ResolveTenantFrequency(
        NotificationDigestFrequency tenantFrequency,
        NotificationDigestFrequency? userFrequency)
    {
        if (tenantFrequency == NotificationDigestFrequency.Disabled ||
            !userFrequency.HasValue ||
            userFrequency == NotificationDigestFrequency.Disabled)
        {
            return tenantFrequency;
        }

        return tenantFrequency switch
        {
            NotificationDigestFrequency.Daily when userFrequency == NotificationDigestFrequency.Weekly => NotificationDigestFrequency.Weekly,
            _ => tenantFrequency
        };
    }

    private static (DateTime StartUtc, DateTime EndUtc) CalculatePeriod(
        DateTime utcNow,
        NotificationDigestFrequency frequency,
        string timeZoneId)
    {
        var zone = IanaTimeZoneRules.Find(timeZoneId);
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, zone);
        var startLocal = localNow.Date;
        if (frequency == NotificationDigestFrequency.Weekly)
        {
            var daysSinceMonday = ((int)startLocal.DayOfWeek + 6) % 7;
            startLocal = startLocal.AddDays(-daysSinceMonday);
        }

        var endLocal = frequency == NotificationDigestFrequency.Weekly
            ? startLocal.AddDays(7)
            : startLocal.AddDays(1);
        return (
            IanaTimeZoneRules.ConvertLocalBoundaryToUtc(startLocal, zone, preferLatestAmbiguousInstant: false),
            IanaTimeZoneRules.ConvertLocalBoundaryToUtc(endLocal, zone, preferLatestAmbiguousInstant: true));
    }

    private static bool IsMandatoryBypass(string? category)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return false;
        }

        return category.StartsWith("Security", StringComparison.OrdinalIgnoreCase) ||
               category.StartsWith("Urgent", StringComparison.OrdinalIgnoreCase) ||
               category.StartsWith("Notifications.Digest", StringComparison.OrdinalIgnoreCase);
    }
}
