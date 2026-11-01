namespace Notifications.Application.Contracts.Channels;

public interface ISmsNotificationSender
{
    Task<SmsSendOutcome> SendAsync(
        string phoneNumber,
        string message,
        CancellationToken cancellationToken = default);
}

/// <summary>Outcome of a channel-level SMS send: the provider message id plus which scoped
/// provider configuration (if any) produced it, so the delivery row can record it for later
/// webhook signature resolution.</summary>
public sealed class SmsSendOutcome
{
    public string? ProviderMessageId { get; init; }

    public Guid? ProviderConfigId { get; init; }
}
