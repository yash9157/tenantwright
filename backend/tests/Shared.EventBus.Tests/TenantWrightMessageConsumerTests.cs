using MassTransit;
using MassTransit.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using RabbitMQ.Client;
using Shared.EventBus.MassTransit;
using Shared.EventBus.Messaging;
using Shared.EventContracts;
using Xunit;

namespace Shared.EventBus.Tests;

/// <summary>
/// The MassTransit adapter: it must translate a RabbitMQ delivery into the neutral inbound descriptor faithfully and add
/// no trust decision of its own.
/// </summary>
public sealed class TenantWrightMessageConsumerTests
{
    [Fact]
    public async Task Consume_should_adapt_the_rabbitmq_delivery_and_forward_it_through_the_neutral_pipeline()
    {
        using var fixture = new NeutralTrustFixture();
        var message = NeutralTrustFixture.HostMessage();
        var stamped = fixture.Stamper().Stamp(message, TenantWrightMessageRouteKind.Event);
        var handler = new RecordingHandler();
        var inbox = new InMemoryInboxTransaction(
            new ServiceCollection().AddSingleton<ITenantWrightMessageHandler<TenantDeactivatedEto>>(handler).BuildServiceProvider());
        var pipeline = fixture.Pipeline(new MessagingTestCurrentTenant(), inbox);
        using var cancellation = new CancellationTokenSource();
        var context = CreateContext(stamped, message, NeutralTrustFixture.ProducerPrincipal, cancellation.Token);

        await new TenantWrightMessageConsumer<TenantDeactivatedEto>(pipeline).Consume(context);

        Assert.Same(message, handler.Received);
        Assert.Equal(cancellation.Token, handler.Token);
    }

    [Fact]
    public async Task Consume_should_reject_a_delivery_whose_broker_identity_is_not_the_signed_producer()
    {
        using var fixture = new NeutralTrustFixture();
        var message = NeutralTrustFixture.HostMessage();
        var stamped = fixture.Stamper().Stamp(message, TenantWrightMessageRouteKind.Event);
        var handler = new RecordingHandler();
        var inbox = new InMemoryInboxTransaction(
            new ServiceCollection().AddSingleton<ITenantWrightMessageHandler<TenantDeactivatedEto>>(handler).BuildServiceProvider());
        var context = CreateContext(stamped, message, NeutralTrustFixture.UntrustedPrincipal, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<MessagingSecurityException>(() =>
            new TenantWrightMessageConsumer<TenantDeactivatedEto>(fixture.Pipeline(new MessagingTestCurrentTenant(), inbox))
                .Consume(context));

        Assert.Equal(MessagingSecurityException.UntrustedProducer, exception.Message);
        Assert.Null(handler.Received);
    }

    [Fact]
    public async Task Consume_without_a_rabbitmq_delivery_should_fail_closed_before_the_pipeline()
    {
        using var fixture = new NeutralTrustFixture();
        var inbox = new InMemoryInboxTransaction(new ServiceCollection().BuildServiceProvider());
        var context = new Mock<ConsumeContext<TenantDeactivatedEto>>(MockBehavior.Strict);
        RabbitMqBasicConsumeContext? missing = null;
        context.Setup(candidate => candidate.TryGetPayload(out missing)).Returns(false);

        var exception = await Assert.ThrowsAsync<MessagingSecurityException>(() =>
            new TenantWrightMessageConsumer<TenantDeactivatedEto>(fixture.Pipeline(new MessagingTestCurrentTenant(), inbox))
                .Consume(context.Object));

        Assert.Equal(MessagingSecurityException.UntrustedProducer, exception.Message);
        Assert.Equal(0, inbox.AttemptCount);
    }

    [Fact]
    public async Task Relay_headers_present_only_in_the_rabbitmq_properties_should_still_count_as_custody_evidence()
    {
        using var fixture = new NeutralTrustFixture();
        var message = NeutralTrustFixture.HostMessage();
        var stamped = fixture.Stamper().Stamp(message, TenantWrightMessageRouteKind.Event);
        var inbox = new InMemoryInboxTransaction(new ServiceCollection().BuildServiceProvider());
        var context = CreateContext(
            stamped,
            message,
            NeutralTrustFixture.ProducerPrincipal,
            CancellationToken.None,
            rawOnlyHeaders: new Dictionary<string, object?> { [TenantWrightTrustHeaders.RelaySignature] = "forged" });

        // A smuggled relay header turns a direct delivery into an incomplete custody claim, which is refused.
        var exception = await Assert.ThrowsAsync<MessagingSecurityException>(() =>
            new TenantWrightMessageConsumer<TenantDeactivatedEto>(fixture.Pipeline(new MessagingTestCurrentTenant(), inbox))
                .Consume(context));

        Assert.Equal(MessagingSecurityException.UntrustedProducer, exception.Message);
        Assert.Equal(0, inbox.AttemptCount);
    }

    private static ConsumeContext<TenantDeactivatedEto> CreateContext(
        TenantWrightOutboundMessage stamped,
        TenantDeactivatedEto message,
        string userId,
        CancellationToken cancellationToken,
        IDictionary<string, object?>? rawOnlyHeaders = null)
    {
        var envelopeHeaders = new DictionarySendHeaders();
        foreach (var header in stamped.Headers)
            envelopeHeaders.Set(header.Key, header.Value);

        var rawHeaders = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var header in rawOnlyHeaders ?? new Dictionary<string, object?>())
            rawHeaders[header.Key] = header.Value;
        var rabbit = new Mock<RabbitMqBasicConsumeContext>(MockBehavior.Strict);
        rabbit.SetupGet(candidate => candidate.Properties)
            .Returns(new BasicProperties { UserId = userId, Headers = rawHeaders });

        var receive = new Mock<ReceiveContext>(MockBehavior.Strict);
        receive.SetupGet(candidate => candidate.InputAddress)
            .Returns(new Uri($"rabbitmq://localhost/vhost/{NeutralTrustFixture.Endpoint}"));

        var context = new Mock<ConsumeContext<TenantDeactivatedEto>>(MockBehavior.Strict);
        var rabbitContext = rabbit.Object;
        context.Setup(candidate => candidate.TryGetPayload(out rabbitContext)).Returns(true);
        context.SetupGet(candidate => candidate.ReceiveContext).Returns(receive.Object);
        context.SetupGet(candidate => candidate.MessageId).Returns(stamped.MessageId);
        context.SetupGet(candidate => candidate.Message).Returns(message);
        context.SetupGet(candidate => candidate.Headers).Returns(envelopeHeaders);
        context.SetupGet(candidate => candidate.CancellationToken).Returns(cancellationToken);
        return context.Object;
    }

    private sealed class RecordingHandler : ITenantWrightMessageHandler<TenantDeactivatedEto>
    {
        public TenantDeactivatedEto? Received { get; private set; }
        public CancellationToken Token { get; private set; }

        public Task HandleAsync(TenantDeactivatedEto message, CancellationToken cancellationToken)
        {
            Received = message;
            Token = cancellationToken;
            return Task.CompletedTask;
        }
    }
}
