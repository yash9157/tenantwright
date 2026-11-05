using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using Shared.EventBus.Messaging;
using Wolverine;
using Wolverine.Runtime;

namespace Shared.EventBus.Wolverine;

/// <summary>Signs and confirms terminal custody before the original delivery can be acknowledged.</summary>
internal sealed class WolverineTerminalPublisher(
    RabbitMqConfirmedPublisher publisher,
    TenantWrightAttestationService attestations,
    TenantWrightMessageCatalog catalog,
    TimeProvider timeProvider,
    IHostApplicationLifetime applicationLifetime)
{
    private int _blockedPublications;

    internal bool HasBlockedPublications => Volatile.Read(ref _blockedPublications) > 0;
    internal CancellationToken ApplicationStopping => applicationLifetime.ApplicationStopping;
    internal void BeginPublicationRecovery() => Interlocked.Increment(ref _blockedPublications);
    internal void EndPublicationRecovery() => Interlocked.Decrement(ref _blockedPublications);

    public WolverineTerminalPublication CreatePublication(Envelope envelope)
    {
        var delivery = envelope.RoutingInformation as TenantWrightRabbitMqDelivery
            ?? throw new MessagingSecurityException(MessagingSecurityException.UntrustedCustody);
        var endpoint = catalog.GetReceiveEndpoint(delivery.Endpoint);
        if (!string.Equals(endpoint.OwnerPrincipal, attestations.Actor, StringComparison.Ordinal))
            throw new MessagingSecurityException(MessagingSecurityException.UntrustedCustody);

        var properties = CreateProperties(envelope, delivery, attestations, timeProvider.GetUtcNow());
        return new WolverineTerminalPublication(
            TenantWrightWolverineTopology.DeadLetterQueueName(endpoint.Name), properties, envelope.Body);
    }

    public Task PublishAsync(WolverineTerminalPublication publication, CancellationToken cancellationToken) =>
        publisher.PublishAsync(publication.Exchange, publication.Properties, publication.Body, cancellationToken);

    internal static BasicProperties CreateProperties(
        Envelope envelope,
        TenantWrightRabbitMqDelivery delivery,
        TenantWrightAttestationService attestations,
        DateTimeOffset now)
    {
        if (envelope.Message is null || delivery.MessageId is not { } messageId)
            throw new MessagingSecurityException(MessagingSecurityException.InvalidContext);
        var identity = TenantWrightMessageContracts.GetIdentity(envelope.Message.GetType());
        var origin = TenantWrightTrustHeaderMap.ReadOrigin(delivery.TrustHeaders, messageId, identity);
        var terminal = attestations.CreateTerminal(
            TenantWrightAttestationService.Sha256(envelope.Body.Span),
            TenantWrightAttestationService.SignatureDigest(origin.Signature),
            null, delivery.Endpoint, Guid.NewGuid().ToString("D"), "fault", now);

        // Only original origin custody and our new terminal signature survive. A failed replay's old authorization
        // must never become authority for another attempt, and broker/Wolverine metadata is not replay material.
        var headers = TenantWrightTrustHeaderMap.FromOrigin(origin)
            .ToDictionary(pair => pair.Key, object? (pair) => pair.Value, StringComparer.Ordinal);
        headers[TenantWrightTrustHeaders.TerminalRawMessageSha256] = terminal.RawMessageSha256;
        headers[TenantWrightTrustHeaders.TerminalOriginChainSha256] = terminal.OriginChainSha256;
        headers[TenantWrightTrustHeaders.TerminalRelayChainSha256] = string.Empty;
        headers[TenantWrightTrustHeaders.TerminalId] = terminal.TerminalId;
        headers[TenantWrightTrustHeaders.TerminalClassification] = terminal.Classification;
        headers[TenantWrightTrustHeaders.TerminalTimestamp] = TenantWrightTrustHeaderMap.FormatInstant(now);
        headers[TenantWrightTrustHeaders.TerminalKeyId] = terminal.KeyId;
        headers[TenantWrightTrustHeaders.TerminalSignature] = terminal.Signature;
        return new BasicProperties
        {
            MessageId = messageId.ToString("D"), UserId = terminal.Actor, Type = identity,
            ContentType = TenantWrightCanonicalMessageSerializer.JsonContentType,
            DeliveryMode = DeliveryModes.Persistent, Headers = headers,
            Timestamp = new AmqpTimestamp(now.ToUnixTimeSeconds())
        };
    }
}

internal sealed record WolverineTerminalPublication(string Exchange, BasicProperties Properties, ReadOnlyMemory<byte> Body);

/// <summary>
/// Contains every failure of terminal publication: Wolverine's last-resort continuation handler can acknowledge a
/// thrown exception. Failed/ambiguous publication retains the unacknowledged delivery and retries publication with
/// capped backoff until confirmation or shutdown. Wolverine's DeferAsync acknowledges before republishing and must
/// never be used here.
/// </summary>
internal static class WolverineTerminalFailure
{
    internal static readonly TimeSpan PublishTimeout = TimeSpan.FromSeconds(30);

    public static async ValueTask ExecuteAsync(IWolverineRuntime runtime, IEnvelopeLifecycle lifecycle, Exception exception)
    {
        var logger = runtime.Logger;
        var envelope = lifecycle.Envelope;
        WolverineTerminalPublisher? publisher = null;
        var recovering = false;
        var backoffSeconds = 1;
        try
        {
            publisher = runtime.Services.GetRequiredService<WolverineTerminalPublisher>();
            // Wolverine cancels its runtime after draining. Host stopping must cancel this retained delivery first,
            // otherwise shutdown would wait for a publication loop that is itself waiting for shutdown.
            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(runtime.Cancellation, publisher.ApplicationStopping);
            // An ambiguous confirmation retries the exact same terminal ID, signature and body in this delivery.
            var publication = publisher.CreatePublication(envelope!);
            while (!stopping.IsCancellationRequested)
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
                    timeout.CancelAfter(PublishTimeout);
                    await publisher.PublishAsync(publication, timeout.Token);
                    await lifecycle.CompleteAsync();
                    logger.LogError("Recorded signed terminal custody for message {MessageId} after handler attempts were exhausted.",
                        envelope!.Id);
                    return;
                }
                catch (Exception) when (!stopping.IsCancellationRequested)
                {
                    if (!recovering)
                    {
                        publisher.BeginPublicationRecovery();
                        recovering = true;
                    }
                    // Exception text can contain protected data; log only a fixed category and message identifier.
                    logger.LogWarning("Terminal custody was not settled for message {MessageId}; retaining the unacknowledged delivery.", envelope?.Id);
                    await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), stopping.Token);
                    backoffSeconds = Math.Min(backoffSeconds * 2, 30);
                }
            }
        }
        catch (Exception)
        {
            // Includes shutdown cancellation. Never let Wolverine's outer exception containment ack the original.
            logger.LogWarning("Message {MessageId} remains unsettled and requires channel recovery.", envelope?.Id);
        }
        finally
        {
            if (recovering)
                publisher!.EndPublicationRecovery();
        }
    }
}
