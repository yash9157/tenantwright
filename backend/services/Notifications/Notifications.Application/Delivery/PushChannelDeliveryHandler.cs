using Notifications.Application.Contracts.Channels;
using Notifications.Application.Contracts.Delivery;
using Notifications.Application.Contracts.Templates;
using Notifications.Domain.Shared.Constants;
using Notifications.Domain.Shared.Enums;
using Shared.Modularity.DependencyInjection;

namespace Notifications.Application.Delivery;

[DisableConventionalRegistration]
public sealed class PushChannelDeliveryHandler : IChannelDeliveryHandler, ITransientDependency
{
    private readonly INotificationsTemplateRenderer _templateRenderer;
    private readonly IPushNotificationSender _sender;

    public PushChannelDeliveryHandler(
        INotificationsTemplateRenderer templateRenderer,
        IPushNotificationSender sender)
    {
        _templateRenderer = templateRenderer;
        _sender = sender;
    }

    public NotificationChannel Channel => NotificationChannel.Push;

    public async Task<ChannelDeliveryResult> DeliverAsync(
        ChannelDeliveryContext context,
        CancellationToken cancellationToken = default)
    {
        if (context.RecipientType != NotificationRecipientType.DeviceToken)
        {
            return ChannelDeliveryResult.PermanentRejection("invalid_push_recipient_type");
        }

        var rendered = await _templateRenderer.RenderAsync(
            context.TemplateName,
            new Dictionary<string, string?>(context.TemplateModel, StringComparer.Ordinal),
            cancellationToken);
        var body = NotificationTextFormatter.ToPlainText(rendered.Content);
        var title = context.Subject?.Trim() ?? rendered.Subject?.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            title = context.TemplateName;
        }

        var outcome = await _sender.SendAsync(
            context.RecipientValue,
            title!,
            body,
            cancellationToken);

        return ChannelDeliveryResult.FromProvider(outcome);
    }
}
