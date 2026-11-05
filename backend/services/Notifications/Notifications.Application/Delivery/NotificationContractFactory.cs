using Notifications.Application.Contracts.Delivery;
using Shared.EventContracts;

namespace Notifications.Application.Delivery;

internal static class NotificationContractFactory
{
    public static Task<NotificationScheduleResult> ScheduleAsync(
        INotificationDeliveryScheduler scheduler,
        Guid? tenantId,
        NotificationChannelRequestHost template,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(template);

        return tenantId.HasValue && tenantId.Value != Guid.Empty
            ? scheduler.ScheduleAsync(CreateTenant(tenantId.Value, template), cancellationToken)
            : scheduler.ScheduleAsync(template, cancellationToken);
    }

    public static Task<NotificationScheduleResult> ScheduleAtAsync(
        INotificationDeliveryScheduler scheduler,
        Guid? tenantId,
        NotificationChannelRequestHost template,
        DateTime notBeforeUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(template);

        return tenantId.HasValue && tenantId.Value != Guid.Empty
            ? scheduler.ScheduleAtAsync(CreateTenant(tenantId.Value, template), notBeforeUtc, cancellationToken)
            : scheduler.ScheduleAtAsync(template, notBeforeUtc, cancellationToken);
    }

    private static NotificationChannelRequestTenant CreateTenant(
        Guid tenantId,
        NotificationChannelRequestHost source)
    {
        return new NotificationChannelRequestTenant
        {
            TenantId = tenantId,
            RequestId = source.RequestId,
            DispatchStepId = source.DispatchStepId,
            SchedulingClaimId = source.SchedulingClaimId,
            DigestBucketId = source.DigestBucketId,
            Channel = source.Channel,
            Subject = source.Subject,
            TemplateName = source.TemplateName,
            TemplateModel = source.TemplateModel,
            Recipients = source.Recipients,
            Category = source.Category,
            Priority = source.Priority,
            ActionUrl = source.ActionUrl,
            OccurredAtUtc = source.OccurredAtUtc,
            ScheduledForUtc = source.ScheduledForUtc,
            SourceService = source.SourceService,
            RespectUserPreferences = source.RespectUserPreferences,
            PreferenceUserId = source.PreferenceUserId,
            PreferenceKey = source.PreferenceKey
        };
    }
}
