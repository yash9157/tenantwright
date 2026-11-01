using Notifications.Domain.Shared.Constants;
using Notifications.Domain.Shared.Enums;
using Notifications.Application.Contracts.Delivery;
using Shared.Modularity.DependencyInjection;

namespace Notifications.Application.Delivery;

[DisableConventionalRegistration]
public sealed class AppChannelDeliveryHandler : IChannelDeliveryHandler, ITransientDependency
{
    public NotificationChannel Channel => NotificationChannel.App;

    public Task<ChannelDeliveryResult> DeliverAsync(
        ChannelDeliveryContext context,
        CancellationToken cancellationToken = default)
    {
        if (context.RecipientType != NotificationRecipientType.UserId)
        {
            return Task.FromResult(ChannelDeliveryResult.Failed("invalid_app_recipient_type"));
        }

        return Task.FromResult(ChannelDeliveryResult.Success(userId: context.RecipientValue));
    }
}
