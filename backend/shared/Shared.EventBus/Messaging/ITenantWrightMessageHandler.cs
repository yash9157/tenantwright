namespace Shared.EventBus.Messaging;

/// <summary>Handles one distributed contract. Transport adapters invoke it after trust verification and tenant restoration, inside the consuming unit of work.</summary>
public interface ITenantWrightMessageHandler<in TMessage> where TMessage : class
{
    Task HandleAsync(TMessage message, CancellationToken cancellationToken);
}
