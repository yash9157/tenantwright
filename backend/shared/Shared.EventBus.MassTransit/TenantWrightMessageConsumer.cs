using MassTransit;
using Shared.EventBus.Messaging;

namespace Shared.EventBus.MassTransit;

/// <summary>
/// The single MassTransit consumer for every TenantWright receive-endpoint contract. It only adapts the MassTransit and
/// RabbitMQ delivery into a <see cref="TenantWrightInboundMessage"/>; trust verification, tenant restoration, inbox
/// deduplication, the handler and its unit of work all run in the neutral <see cref="TenantWrightInboundMessagePipeline"/>.
/// </summary>
public sealed class TenantWrightMessageConsumer<TMessage>(TenantWrightInboundMessagePipeline pipeline) : IConsumer<TMessage>
    where TMessage : class
{
    public Task Consume(ConsumeContext<TMessage> context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.TryGetPayload<RabbitMqBasicConsumeContext>(out var rabbit))
            throw new MessagingSecurityException(MessagingSecurityException.UntrustedProducer);

        var inbound = new TenantWrightInboundMessage(
            context.ReceiveContext.InputAddress.AbsolutePath.Trim('/').Split('/').Last(),
            context.MessageId,
            context.Message,
            ReadTrustHeaders(context, rabbit.Properties.Headers),
            rabbit.Properties.UserId,
            checked(context.GetRedeliveryCount() + 1),
            new RabbitMqTransportEvidence(rabbit.Properties));
        return pipeline.ProcessAsync<TMessage>(inbound, context.CancellationToken);
    }

    /// <summary>
    /// Every reserved trust header present in either the MassTransit envelope or the raw RabbitMQ properties. Presence
    /// in either location counts as custody evidence; values are read from the envelope only.
    /// </summary>
    private static Dictionary<string, string?> ReadTrustHeaders(
        ConsumeContext context,
        IDictionary<string, object?>? rawHeaders)
    {
        var headers = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var name in TenantWrightTrustHeaders.All)
        {
            var inEnvelope = context.Headers.TryGetHeader(name, out var envelopeValue) && envelopeValue is not null;
            var inProperties = rawHeaders?.TryGetValue(name, out var rawValue) == true && rawValue is not null;
            if (!inEnvelope && !inProperties)
                continue;
            try
            {
                headers[name] = context.Headers.Get<string>(name);
            }
            catch (Exception exception) when (exception is FormatException or InvalidCastException)
            {
                throw new MessagingSecurityException(MessagingSecurityException.InvalidContext);
            }
        }
        return headers;
    }
}
