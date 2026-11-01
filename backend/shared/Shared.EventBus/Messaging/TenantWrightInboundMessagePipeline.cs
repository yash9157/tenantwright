using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shared.MultiTenancy;

namespace Shared.EventBus.Messaging;

/// <summary>
/// The durable consume boundary a host's persistence layer provides: one transaction that records the inbox entry and
/// runs the handler, so a duplicate delivery of the same message to the same endpoint never repeats the effect.
/// </summary>
public interface ITenantWrightInboxTransaction
{
    /// <summary>
    /// Records <paramref name="messageId"/> for <paramref name="endpoint"/> and invokes <paramref name="handle"/> with the
    /// transaction's service scope, committing both together. Returns <see langword="false"/> without invoking the
    /// handler when the message was already processed by this endpoint. A handler failure rolls back the inbox entry.
    /// </summary>
    Task<bool> TryProcessOnceAsync(
        Guid messageId,
        string endpoint,
        Func<IServiceProvider, CancellationToken, Task> handle,
        CancellationToken cancellationToken);
}

/// <summary>
/// The transport-neutral inbound pipeline every provider adapter calls: trust verification, tenant restoration, then
/// the inbox transaction in which the contract's <see cref="ITenantWrightMessageHandler{TMessage}"/> runs. Messages the
/// handler sends are written to the outbox inside the same transaction.
/// </summary>
public sealed class TenantWrightInboundMessagePipeline(
    TenantWrightInboundTrustVerifier verifier,
    ICurrentTenant currentTenant,
    ITenantWrightInboxTransaction inbox,
    ILogger<TenantWrightInboundMessagePipeline> logger)
{
    public async Task ProcessAsync<TMessage>(TenantWrightInboundMessage inbound, CancellationToken cancellationToken)
        where TMessage : class
    {
        ArgumentNullException.ThrowIfNull(inbound);
        if (inbound.Message is not TMessage message || inbound.MessageType != typeof(TMessage))
            throw new MessagingSecurityException(MessagingSecurityException.UnsupportedContract);

        TenantWrightVerifiedInboundMessage verified;
        try
        {
            verified = await verifier.VerifyAsync(inbound, cancellationToken);
        }
        catch (MessagingSecurityException exception)
        {
            TenantWrightMessagingMetrics.InboxRejected.Add(1,
                new KeyValuePair<string, object?>("endpoint", inbound.EndpointName),
                new KeyValuePair<string, object?>("reason", exception.Message));
            logger.LogWarning(
                "Rejected an inbound message on {Endpoint}: {Reason} Contract={Contract} Attempt={Attempt}",
                inbound.EndpointName,
                exception.Message,
                typeof(TMessage).FullName,
                inbound.DeliveryAttempt);
            throw;
        }

        using (currentTenant.Change(verified.TenantId))
        {
            var processed = await inbox.TryProcessOnceAsync(
                verified.MessageId,
                verified.Endpoint,
                (services, token) => services
                    .GetRequiredService<ITenantWrightMessageHandler<TMessage>>()
                    .HandleAsync(message, token),
                cancellationToken);

            var endpointTag = new KeyValuePair<string, object?>("endpoint", verified.Endpoint);
            if (processed)
            {
                TenantWrightMessagingMetrics.InboxProcessed.Add(1, endpointTag);
                return;
            }

            TenantWrightMessagingMetrics.InboxDuplicate.Add(1, endpointTag);
            logger.LogInformation(
                "Acknowledged a duplicate delivery of message {MessageId} on {Endpoint} without invoking its handler.",
                verified.MessageId,
                verified.Endpoint);
        }
    }
}
