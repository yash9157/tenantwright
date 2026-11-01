using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shared.EventBus.Messaging;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Shared.EventBus.Wolverine;

/// <summary>
/// The single Wolverine handler for one TenantWright receive-endpoint contract, registered pre-built so Wolverine generates
/// no code for it. It only adapts the RabbitMQ delivery into a <see cref="TenantWrightInboundMessage"/>; trust
/// verification, tenant restoration, inbox deduplication, the handler and its unit of work all run in the neutral
/// <see cref="TenantWrightInboundMessagePipeline"/>, and the delivery is acknowledged only after the pipeline returns.
/// </summary>
/// <remarks>
/// One instance serves every endpoint that consumes <typeparamref name="TMessage"/>: the endpoint comes from the
/// delivery the endpoint's own mapper captured, never from the wire.
/// </remarks>
internal sealed class TenantWrightWolverineMessageHandler<TMessage> : MessageHandler<TMessage>
    where TMessage : class
{
    public override void ConfigureChain(HandlerChain chain)
    {
        ArgumentNullException.ThrowIfNull(chain);

        // Every failure is either a rejected message (already logged by the pipeline at Warning) or a retried
        // transient failure; Error is reserved for the dead-letter outcome Wolverine reports itself.
        chain.FailureLogLevel = LogLevel.Warning;
    }

    protected override async Task HandleAsync(TMessage message, MessageContext context, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(context);

        var envelope = context.Envelope
            ?? throw new MessagingSecurityException(MessagingSecurityException.InvalidContext);
        if (envelope.RoutingInformation is not TenantWrightRabbitMqDelivery delivery)
            throw new MessagingSecurityException(MessagingSecurityException.UntrustedCustody);

        var inbound = new TenantWrightInboundMessage(
            delivery.Endpoint,
            delivery.MessageId,
            message,
            delivery.TrustHeaders,
            delivery.BrokerActor,
            Math.Max(envelope.Attempts, 1),
            delivery.TransportEvidence);

        try
        {
            await using var scope = context.Runtime.Services.CreateAsyncScope();
            await scope.ServiceProvider
                .GetRequiredService<TenantWrightInboundMessagePipeline>()
                .ProcessAsync<TMessage>(inbound, cancellation);
        }
        catch (OperationCanceledException) when (context.Runtime.Cancellation.IsCancellationRequested)
        {
            throw new TenantWrightConsumeInterruptedException();
        }
    }
}

/// <summary>
/// The host stopped while a delivery was being handled. The failure policy leaves the delivery unsettled, so the broker
/// requeues it when the channel closes and the inbox deduplicates the redelivery; it is never retried in process or
/// dead-lettered.
/// </summary>
internal sealed class TenantWrightConsumeInterruptedException : Exception
{
    public TenantWrightConsumeInterruptedException()
        : base("The host stopped while the delivery was being handled.")
    {
    }
}
