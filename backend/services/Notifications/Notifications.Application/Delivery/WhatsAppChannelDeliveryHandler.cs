using Notifications.Application.Contracts.Channels;
using Notifications.Application.Contracts.Delivery;
using Notifications.Application.Contracts.Templates;
using Notifications.Domain.Shared.Constants;
using Notifications.Domain.Shared.Enums;
using Shared.Modularity.DependencyInjection;

namespace Notifications.Application.Delivery;

[DisableConventionalRegistration]
public sealed class WhatsAppChannelDeliveryHandler : IChannelDeliveryHandler, ITransientDependency
{
    private readonly INotificationsTemplateRenderer _templateRenderer;
    private readonly IWhatsAppNotificationSender _sender;

    public WhatsAppChannelDeliveryHandler(
        INotificationsTemplateRenderer templateRenderer,
        IWhatsAppNotificationSender sender)
    {
        _templateRenderer = templateRenderer;
        _sender = sender;
    }

    public NotificationChannel Channel => NotificationChannel.WhatsApp;

    public async Task<ChannelDeliveryResult> DeliverAsync(
        ChannelDeliveryContext context,
        CancellationToken cancellationToken = default)
    {
        if (context.RecipientType != NotificationRecipientType.PhoneNumber)
        {
            return ChannelDeliveryResult.Failed("invalid_whatsapp_recipient_type");
        }

        var rendered = await _templateRenderer.RenderAsync(
            context.TemplateName,
            new Dictionary<string, string?>(context.TemplateModel, StringComparer.Ordinal),
            cancellationToken);
        var message = NotificationTextFormatter.ToPlainText(rendered.Content);
        var header = context.Subject?.Trim() ?? rendered.Subject?.Trim();
        if (!string.IsNullOrWhiteSpace(header))
        {
            message = $"{header}{Environment.NewLine}{message}";
        }

        var outcome = await _sender.SendAsync(context.RecipientValue, context.TemplateName, message, cancellationToken);
        return ChannelDeliveryResult.Success(providerMessageId: outcome.ProviderMessageId, providerConfigId: outcome.ProviderConfigId);
    }
}
