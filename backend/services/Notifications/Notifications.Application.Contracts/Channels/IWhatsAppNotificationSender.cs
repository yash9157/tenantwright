namespace Notifications.Application.Contracts.Channels;

public interface IWhatsAppNotificationSender
{
    Task<WhatsAppSendOutcome> SendAsync(
        string phoneNumber,
        string templateName,
        string message,
        CancellationToken cancellationToken = default);
}

/// <summary>Outcome of a channel-level WhatsApp send: the provider message id plus which scoped
/// provider configuration (if any) produced it.</summary>
public sealed class WhatsAppSendOutcome
{
    public string? ProviderMessageId { get; init; }

    public Guid? ProviderConfigId { get; init; }
}
