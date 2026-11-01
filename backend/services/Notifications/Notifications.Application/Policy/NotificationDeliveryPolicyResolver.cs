using Microsoft.EntityFrameworkCore;
using Notifications.Domain.Notifications;
using Notifications.Domain.Policy;
using Notifications.Domain.Shared.Enums;
using Notifications.EntityFrameworkCore.EntityFrameworkCore;
using Shared.Modularity.DependencyInjection;
using Shared.EntityFrameworkCore;
namespace Notifications.Application.Policy;

internal sealed class NotificationDeliveryPolicyResolver : ITransientDependency
{
    private readonly IDbContextProvider<NotificationsDbContextApp> _dbContextProvider;

    public NotificationDeliveryPolicyResolver(IDbContextProvider<NotificationsDbContextApp> dbContextProvider)
    {
        _dbContextProvider = dbContextProvider;
    }

    public async Task<NotificationPolicyDecision> EvaluateAsync(
        Notification notification,
        NotificationRecipient recipient,
        DateTime atUtc,
        CancellationToken cancellationToken)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        TenantNotificationPolicy? policy = null;
        if (notification.TenantId.HasValue)
        {
            policy = await unitOfWorkDbContext.TenantNotificationPolicies
                .IgnoreQueryFilters()
                .AsNoTracking()
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
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    x => x.TenantId == notification.TenantId && x.UserId == userId,
                    cancellationToken);
        }

        return NotificationPolicyCalculator.Evaluate(
            notification.Channel,
            notification.Category,
            notification.Priority,
            atUtc,
            policy,
            preference);
    }
}
