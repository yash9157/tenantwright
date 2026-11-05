using System.Net;
using Notifications.Application.Contracts.Emailing;
using Notifications.Application.Contracts.Delivery;
using Notifications.Application.Contracts.Templates;
using Notifications.Domain.Shared.Constants;
using Notifications.Domain.Shared.Enums;
using Shared.Modularity.DependencyInjection;

namespace Notifications.Application.Delivery;

[DisableConventionalRegistration]
public sealed class EmailChannelDeliveryHandler : IChannelDeliveryHandler, ITransientDependency
{
    private readonly INotificationsTemplateRenderer _templateRenderer;
    private readonly IEmailNotificationSender _emailSender;

    public EmailChannelDeliveryHandler(
        INotificationsTemplateRenderer templateRenderer,
        IEmailNotificationSender emailSender)
    {
        _templateRenderer = templateRenderer;
        _emailSender = emailSender;
    }

    public NotificationChannel Channel => NotificationChannel.Email;

    public async Task<ChannelDeliveryResult> DeliverAsync(
        ChannelDeliveryContext context,
        CancellationToken cancellationToken = default)
    {
        if (context.RecipientType != NotificationRecipientType.EmailAddress)
        {
            return ChannelDeliveryResult.PermanentRejection("invalid_email_recipient_type");
        }

        var model = context.TemplateModel.ToDictionary(
            pair => pair.Key,
            pair => pair.Value == null ? null : WebUtility.HtmlEncode(pair.Value),
            StringComparer.Ordinal);
        model["recipient"] = WebUtility.HtmlEncode(context.RecipientValue);

        var rendered = await _templateRenderer.RenderAsync(
            context.TemplateName,
            model,
            cancellationToken);

        var result = await _emailSender.SendAsync(
            context.RecipientValue,
            context.Subject?.Trim() ?? rendered.Subject ?? string.Empty,
            rendered.Content,
            cancellationToken);

        return ChannelDeliveryResult.FromProvider(result);
    }
}
