namespace Shared.EventBus.Messaging;

/// <summary>
/// The only application-facing way to emit a distributed message. Hosts with persistence record the stamped message
/// in the durable outbox inside the caller's unit of work; the active transport provider delivers it later.
/// </summary>
public interface ITenantWrightMessageSender
{
    Task SendCommandAsync<T>(T command, CancellationToken cancellationToken = default) where T : class;
    Task PublishEventAsync<T>(T message, CancellationToken cancellationToken = default) where T : class;
}
