using Shared.EventBus.Messaging;
using Shared.EventContracts;

namespace Identity.Application.Account;

internal static class IdentityNotificationContractPublisher
{
    public static Task PublishAsync(
        ITenantWrightMessageSender eventBus,
        Guid? tenantId,
        NotificationSendRequestedHostEto template,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventBus);
        ArgumentNullException.ThrowIfNull(template);

        return tenantId.HasValue && tenantId.Value != Guid.Empty
            ? eventBus.SendCommandAsync(
                CreateTenant(tenantId.Value, template),
                cancellationToken: cancellationToken)
            : eventBus.SendCommandAsync(template, cancellationToken: cancellationToken);
    }

    private static NotificationSendRequestedTenantEto CreateTenant(
        Guid tenantId,
        NotificationSendRequestedHostEto source)
    {
        return new NotificationSendRequestedTenantEto
        {
            TenantId = tenantId,
            RequestId = source.RequestId,
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
