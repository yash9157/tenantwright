using Microsoft.Extensions.Options;
using Notifications.Application.Contracts.Channels;
using Notifications.Application.Contracts.Delivery;
using Shared.Modularity.DependencyInjection;

namespace Notifications.Application.Delivery;

public sealed class ConfigurableWhatsAppNotificationSender : IWhatsAppNotificationSender, ITransientDependency
{
    private readonly IOptions<NotificationsChannelProviderModeOptions> _options;

    public ConfigurableWhatsAppNotificationSender(IOptions<NotificationsChannelProviderModeOptions> options)
    {
        _options = options;
    }

    public Task<WhatsAppSendOutcome> SendAsync(
        string phoneNumber,
        string templateName,
        string message,
        CancellationToken cancellationToken = default)
    {
        var mode = NotificationProviderModeParser.Parse(_options.Value.WhatsApp);

        switch (mode)
        {
            case NotificationProviderMode.MockSuccess:
                return Task.FromResult(new WhatsAppSendOutcome
                {
                    ProviderMessageId = $"mock-whatsapp-{Guid.CreateVersion7():N}"
                });

            case NotificationProviderMode.Live:
                throw new NotificationChannelUnavailableException(
                    "whatsapp_consent_authority_unavailable",
                    "Live WhatsApp delivery is unavailable until governed opt-in evidence is implemented.");

            default:
                throw new NotificationChannelUnavailableException(
                    "whatsapp_provider_unavailable",
                    "WhatsApp provider is disabled. Set Notifications:Providers:WhatsApp=MockSuccess|Live or configure a real provider.");
        }
    }

}
