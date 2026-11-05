using Notifications.Application.Contracts.Channels;
using Notifications.Application.Contracts.Delivery;
using Notifications.Application.Contracts.Templates;
using Notifications.Domain.Shared.Constants;
using Notifications.Domain.Shared.Enums;
using Shared.Modularity.DependencyInjection;

namespace Notifications.Application.Delivery;

[DisableConventionalRegistration]
public sealed class SmsChannelDeliveryHandler : IChannelDeliveryHandler, ITransientDependency
{
    private readonly INotificationsTemplateRenderer _templateRenderer;
    private readonly ISmsNotificationSender _sender;

    public SmsChannelDeliveryHandler(
        INotificationsTemplateRenderer templateRenderer,
        ISmsNotificationSender sender)
    {
        _templateRenderer = templateRenderer;
        _sender = sender;
    }

    public NotificationChannel Channel => NotificationChannel.Sms;

    public async Task<ChannelDeliveryResult> DeliverAsync(
        ChannelDeliveryContext context,
        CancellationToken cancellationToken = default)
    {
        if (context.RecipientType != NotificationRecipientType.PhoneNumber)
        {
            return ChannelDeliveryResult.PermanentRejection("invalid_sms_recipient_type");
        }

        var rendered = await _templateRenderer.RenderAsync(
            context.TemplateName,
            new Dictionary<string, string?>(context.TemplateModel, StringComparer.Ordinal),
            cancellationToken);
        var message = NotificationTextFormatter.ToPlainText(rendered.Content);
        var outcome = await _sender.SendAsync(context.RecipientValue, message, cancellationToken);
        return ChannelDeliveryResult.FromProvider(outcome);
    }
}
