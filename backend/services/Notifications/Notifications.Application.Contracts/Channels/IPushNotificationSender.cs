namespace Notifications.Application.Contracts.Channels;

public interface IPushNotificationSender
{
    Task<PushSendOutcome> SendAsync(
        string deviceToken,
        string title,
        string body,
        CancellationToken cancellationToken = default);
}

/// <summary>Outcome of a channel-level push send: the provider message id plus which scoped
/// provider configuration (if any) produced it.</summary>
public sealed class PushSendOutcome
{
    public string? ProviderMessageId { get; init; }

    public Guid? ProviderConfigId { get; init; }
}
