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

    public static List<NotificationRecipientPlan> CreateSingleChannelRecipients(NotificationSendChannel channel,
        IEnumerable<NotificationRecipientEto> recipients, Guid? preferenceUserId = null) => recipients.Select((recipient, index) => new NotificationRecipientPlan
        {
            Key = index.ToString(System.Globalization.CultureInfo.InvariantCulture),
            PreferenceUserId = preferenceUserId,
            Destinations = [new NotificationDestination
            {
                Channel = channel, RecipientType = recipient.RecipientType, RecipientValue = recipient.RecipientValue
            }]
        }).ToList();

    private static NotificationSendRequestedTenantEto CreateTenant(
        Guid tenantId,
        NotificationSendRequestedHostEto source)
    {
        return new NotificationSendRequestedTenantEto
        {
            TenantId = tenantId,
            RequestId = source.RequestId,
            Category = source.Category,
            Priority = source.Priority,
            ActionUrl = source.ActionUrl,
            OccurredAtUtc = source.OccurredAtUtc,
            ScheduledForUtc = source.ScheduledForUtc,
            SourceService = source.SourceService,
            RespectUserPreferences = source.RespectUserPreferences,
            PreferenceKey = source.PreferenceKey,
            DeliveryPlan = source.DeliveryPlan,
            Recipients = source.Recipients,
            ExpiresAtUtc = source.ExpiresAtUtc,
        };
    }
}
