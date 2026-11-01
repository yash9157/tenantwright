using MassTransit;
using MassTransit.RabbitMqTransport.Middleware;
using Microsoft.Extensions.Options;
using Shared.EventBus.Messaging;

namespace Shared.EventBus.MassTransit;

internal sealed record TenantWrightRelaySendMarker(
    OriginAttestation Origin,
    RelayAttestation Relay);

internal sealed record TenantWrightReplaySendMarker(
    OriginAttestation Origin,
    RelayAttestation? Relay,
    ReplayAttestation Replay);

/// <summary>
/// The send pipe a MassTransit <see cref="IProtectedReplayTransport"/> passes with one authorized replay. It attaches
/// the replay custody of <see cref="ProtectedReplayDispatch"/> as a payload, which <see cref="TenantWrightSendTrustFilter{T}"/>
/// binds to the wire under the replay principal.
/// </summary>
public sealed class TenantWrightReplaySendPipe(ProtectedReplayDispatch dispatch) : IPipe<SendContext>
{
    private readonly TenantWrightReplaySendMarker _marker = new(
        (dispatch ?? throw new ArgumentNullException(nameof(dispatch))).Material.Origin,
        dispatch.Material.Relay,
        dispatch.Attestation);

    public Task Send(SendContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.GetOrAddPayload(() => _marker);
        return Task.CompletedTask;
    }

    public void Probe(ProbeContext context) => context.CreateFilterScope("tenantwrightReplayMarker");
}

public sealed class TenantWrightRedeliveryTrustFilter<T>(
    TenantWrightMessageCatalog catalog,
    IOptions<MessagingOptions> options) : IFilter<ConsumeContext<T>> where T : class
{
    public async Task Send(ConsumeContext<T> context, IPipe<ConsumeContext<T>> next)
    {
        if (!catalog.TryGetRoute(typeof(T), out _))
        {
            await next.Send(context);
            return;
        }

        var runtimeType = context.Message.GetType();
        catalog.GetRoute(runtimeType);
        var endpointName = context.ReceiveContext.InputAddress.AbsolutePath.Trim('/').Split('/').Last();
        var endpoint = catalog.GetReceiveEndpoint(endpointName);
        if (!endpoint.ContractTypes.Contains(runtimeType))
            throw new MessagingSecurityException(MessagingSecurityException.UntrustedCustody);

        var redelivery = new TenantWrightMessageRedeliveryContext<T>(context, options.Value);
        context.AddOrUpdatePayload<MessageRedeliveryContext>(() => redelivery, _ => redelivery);
        await next.Send(context);
    }

    public void Probe(ProbeContext context) => context.CreateFilterScope("tenantwrightRedeliveryTrust");
}

internal sealed class TenantWrightMessageRedeliveryContext<T>(
    ConsumeContext<T> context,
    MessagingOptions options) : MessageRedeliveryContext where T : class
{
    private readonly QueueBasedMessageRedeliveryContext<T> _inner = new(context, RedeliveryOptions.None);

    public Task ScheduleRedelivery(TimeSpan delay, Action<ConsumeContext, SendContext>? callback = null)
    {
        var attempt = checked(context.GetRedeliveryCount() + 1);
        if (attempt < 1 || attempt > options.Delivery.DelayedRedeliverySeconds.Length ||
            delay != TimeSpan.FromSeconds(options.Delivery.DelayedRedeliverySeconds[attempt - 1]))
            throw new MessagingSecurityException(MessagingSecurityException.UntrustedCustody);
        return _inner.ScheduleRedelivery(delay, callback);
    }
}
