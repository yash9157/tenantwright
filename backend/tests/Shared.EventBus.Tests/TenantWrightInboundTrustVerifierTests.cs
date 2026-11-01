using Shared.EventBus.Messaging;
using Xunit;

namespace Shared.EventBus.Tests;

/// <summary>
/// The neutral inbound trust boundary. Each test builds a real signed delivery and changes exactly one property.
/// </summary>
public sealed class TenantWrightInboundTrustVerifierTests
{
    [Fact]
    public async Task Valid_direct_delivery_should_be_accepted_with_host_scope()
    {
        using var fixture = new NeutralTrustFixture();
        var message = NeutralTrustFixture.HostMessage();
        var stamped = fixture.Stamper().Stamp(message, TenantWrightMessageRouteKind.Event);

        var verified = await fixture.Verifier().VerifyAsync(NeutralTrustFixture.Delivery(stamped, message), default);

        Assert.Equal(TenantWrightInboundCustody.Direct, verified.Custody);
        Assert.Equal(stamped.MessageId, verified.MessageId);
        Assert.Equal(NeutralTrustFixture.Endpoint, verified.Endpoint);
        Assert.Null(verified.TenantId);
    }

    [Fact]
    public async Task Valid_tenant_delivery_should_return_the_signed_tenant_to_restore()
    {
        using var fixture = new NeutralTrustFixture();
        var tenantId = Guid.NewGuid();
        var message = NeutralTrustFixture.TenantMessage(tenantId);
        var stamped = fixture.Stamper(new MessagingTestCurrentTenant(tenantId)).Stamp(message, TenantWrightMessageRouteKind.Event);

        var verified = await fixture.Verifier().VerifyAsync(NeutralTrustFixture.Delivery(stamped, message), default);

        Assert.Equal(tenantId, verified.TenantId);
    }

    [Fact]
    public async Task Tenant_delivery_whose_body_tenant_differs_from_the_signed_tenant_should_be_rejected()
    {
        using var fixture = new NeutralTrustFixture();
        var tenantId = Guid.NewGuid();
        var signedMessage = NeutralTrustFixture.TenantMessage(tenantId);
        var stamped = fixture.Stamper(new MessagingTestCurrentTenant(tenantId)).Stamp(signedMessage, TenantWrightMessageRouteKind.Event);
        var forged = NeutralTrustFixture.TenantMessage(Guid.NewGuid());

        // The body digest no longer matches, so the signature check rejects it before tenancy is even considered.
        await AssertRejectedAsync(fixture.Verifier(), NeutralTrustFixture.Delivery(stamped, forged),
            MessagingSecurityException.InvalidSignature);
    }

    [Fact]
    public async Task Valid_relayed_delivery_should_require_the_provider_redelivery_verifier()
    {
        using var fixture = new NeutralTrustFixture();
        var message = NeutralTrustFixture.HostMessage();
        var stamped = fixture.Stamper().Stamp(message, TenantWrightMessageRouteKind.Event);
        var relay = fixture.Relay(stamped, fixture.Now.AddSeconds(-5), fixture.Now.AddMinutes(10));
        var redelivery = new RecordingRedeliveryCustodyVerifier();
        var delivery = NeutralTrustFixture.Delivery(
            stamped,
            message,
            brokerActor: NeutralTrustFixture.EndpointOwner,
            headers: NeutralTrustFixture.WithHeaders(stamped, TenantWrightTrustHeaderMap.FromRelay(relay)),
            evidence: new TestTransportEvidence(hasRedeliveryHistory: true));

        var verified = await fixture.Verifier(redelivery).VerifyAsync(delivery, default);

        Assert.Equal(TenantWrightInboundCustody.Redelivered, verified.Custody);
        Assert.Equal(relay.Signature, Assert.Single(redelivery.Verified).Signature);
    }

    [Fact]
    public async Task Relay_evidence_without_a_registered_redelivery_verifier_should_be_rejected()
    {
        using var fixture = new NeutralTrustFixture();
        var message = NeutralTrustFixture.HostMessage();
        var stamped = fixture.Stamper().Stamp(message, TenantWrightMessageRouteKind.Event);
        var relay = fixture.Relay(stamped, fixture.Now.AddSeconds(-5), fixture.Now.AddMinutes(10));
        var delivery = NeutralTrustFixture.Delivery(
            stamped,
            message,
            brokerActor: NeutralTrustFixture.EndpointOwner,
            headers: NeutralTrustFixture.WithHeaders(stamped, TenantWrightTrustHeaderMap.FromRelay(relay)),
            evidence: new TestTransportEvidence(hasRedeliveryHistory: true));

        await AssertRejectedAsync(fixture.Verifier(), delivery, MessagingSecurityException.UntrustedCustody);
    }

    [Fact]
    public async Task Expired_relay_should_be_rejected()
    {
        using var fixture = new NeutralTrustFixture();
        var message = NeutralTrustFixture.HostMessage();
        var stamped = fixture.Stamper().Stamp(message, TenantWrightMessageRouteKind.Event);
        var relay = fixture.Relay(stamped, fixture.Now.AddMinutes(-11), fixture.Now.AddSeconds(-1));
        var redelivery = new RecordingRedeliveryCustodyVerifier();
        var delivery = NeutralTrustFixture.Delivery(
            stamped,
            message,
            brokerActor: NeutralTrustFixture.EndpointOwner,
            headers: NeutralTrustFixture.WithHeaders(stamped, TenantWrightTrustHeaderMap.FromRelay(relay)),
            evidence: new TestTransportEvidence(hasRedeliveryHistory: true));

        await AssertRejectedAsync(fixture.Verifier(redelivery), delivery, MessagingSecurityException.UntrustedCustody);
        Assert.Empty(redelivery.Verified);
    }

    [Fact]
    public async Task Relay_signed_by_anyone_but_the_endpoint_owner_should_be_rejected()
    {
        using var fixture = new NeutralTrustFixture();
        var message = NeutralTrustFixture.HostMessage();
        var stamped = fixture.Stamper().Stamp(message, TenantWrightMessageRouteKind.Event);
        var forgedRelay = fixture.Untrusted.CreateRelay(
            stamped.MessageId,
            stamped.Origin.PayloadSha256,
            TenantWrightAttestationService.SignatureDigest(stamped.Origin.Signature),
            NeutralTrustFixture.Endpoint,
            Guid.NewGuid().ToString("D"),
            1,
            fixture.Now.AddSeconds(-5),
            fixture.Now.AddMinutes(10));
        var delivery = NeutralTrustFixture.Delivery(
            stamped,
            message,
            brokerActor: NeutralTrustFixture.EndpointOwner,
            headers: NeutralTrustFixture.WithHeaders(stamped, TenantWrightTrustHeaderMap.FromRelay(forgedRelay)),
            evidence: new TestTransportEvidence(hasRedeliveryHistory: true));

        await AssertRejectedAsync(fixture.Verifier(new RecordingRedeliveryCustodyVerifier()), delivery,
            MessagingSecurityException.UntrustedCustody);
    }

    [Fact]
    public async Task Broker_redelivery_history_without_a_relay_should_be_rejected()
    {
        using var fixture = new NeutralTrustFixture();
        var message = NeutralTrustFixture.HostMessage();
        var stamped = fixture.Stamper().Stamp(message, TenantWrightMessageRouteKind.Event);
        var delivery = NeutralTrustFixture.Delivery(
            stamped,
            message,
            brokerActor: NeutralTrustFixture.ProducerPrincipal,
            evidence: new TestTransportEvidence(hasRedeliveryHistory: true));

        await AssertRejectedAsync(fixture.Verifier(new RecordingRedeliveryCustodyVerifier()), delivery,
            MessagingSecurityException.UntrustedProducer);
    }

    [Fact]
    public async Task Tampered_payload_should_be_rejected()
    {
        using var fixture = new NeutralTrustFixture();
        var message = NeutralTrustFixture.HostMessage();
        var stamped = fixture.Stamper().Stamp(message, TenantWrightMessageRouteKind.Event);
        var tampered = NeutralTrustFixture.HostMessage();
        tampered.TenantName = "Tampered";

        await AssertRejectedAsync(fixture.Verifier(), NeutralTrustFixture.Delivery(stamped, tampered),
            MessagingSecurityException.InvalidSignature);
    }

    [Fact]
    public async Task Tampered_signature_should_be_rejected()
    {
        using var fixture = new NeutralTrustFixture();
        var message = NeutralTrustFixture.HostMessage();
        var stamped = fixture.Stamper().Stamp(message, TenantWrightMessageRouteKind.Event);
        var forgedSignature = Convert.ToBase64String(fixture.UntrustedSigner.Sign("forged"u8));
        var headers = NeutralTrustFixture.WithHeaders(stamped);
        headers[TenantWrightTrustHeaders.OriginSignature] = forgedSignature;

        await AssertRejectedAsync(fixture.Verifier(), NeutralTrustFixture.Delivery(stamped, message, headers: headers),
            MessagingSecurityException.InvalidSignature);
    }

    [Fact]
    public async Task Producer_outside_the_route_allow_list_should_be_rejected_even_with_a_valid_signature()
    {
        using var fixture = new NeutralTrustFixture();
        var message = NeutralTrustFixture.HostMessage();
        var payload = fixture.Catalog.SerializeCanonical(message, message.GetType());
        var origin = fixture.Untrusted.CreateOrigin(
            Guid.NewGuid(), "Shared.EventContracts.TenantDeactivatedEto", "host", null, fixture.Now,
            TenantWrightAttestationService.Sha256(payload));
        var delivery = new TenantWrightInboundMessage(
            NeutralTrustFixture.Endpoint,
            origin.MessageId,
            message,
            TenantWrightTrustHeaderMap.FromOrigin(origin).ToDictionary(h => h.Key, string? (h) => h.Value),
            NeutralTrustFixture.UntrustedPrincipal,
            1,
            NoTenantWrightTransportEvidence.Instance);

        await AssertRejectedAsync(fixture.Verifier(), delivery, MessagingSecurityException.UntrustedProducer);
    }

    [Fact]
    public async Task Broker_actor_other_than_the_origin_producer_should_be_rejected()
    {
        using var fixture = new NeutralTrustFixture();
        var message = NeutralTrustFixture.HostMessage();
        var stamped = fixture.Stamper().Stamp(message, TenantWrightMessageRouteKind.Event);

        await AssertRejectedAsync(fixture.Verifier(),
            NeutralTrustFixture.Delivery(stamped, message, brokerActor: NeutralTrustFixture.UntrustedPrincipal),
            MessagingSecurityException.UntrustedProducer);
        await AssertRejectedAsync(fixture.Verifier(),
            NeutralTrustFixture.Delivery(stamped, message, brokerActor: null),
            MessagingSecurityException.UntrustedProducer);
    }

    [Fact]
    public async Task Stale_and_future_origins_should_be_rejected()
    {
        using var fixture = new NeutralTrustFixture();
        var message = NeutralTrustFixture.HostMessage();
        var stale = fixture.Stamper(time: new FixedMessagingTimeProvider(fixture.Now.AddHours(-24).AddSeconds(-1)))
            .Stamp(message, TenantWrightMessageRouteKind.Event);
        var future = fixture.Stamper(time: new FixedMessagingTimeProvider(fixture.Now.AddMinutes(3)))
            .Stamp(message, TenantWrightMessageRouteKind.Event);

        await AssertRejectedAsync(fixture.Verifier(), NeutralTrustFixture.Delivery(stale, message),
            MessagingSecurityException.InvalidContext);
        await AssertRejectedAsync(fixture.Verifier(), NeutralTrustFixture.Delivery(future, message),
            MessagingSecurityException.InvalidContext);
    }

    [Theory]
    [InlineData(TenantWrightTrustHeaders.ContextVersion)]
    [InlineData(TenantWrightTrustHeaders.Producer)]
    [InlineData(TenantWrightTrustHeaders.OriginSignature)]
    [InlineData(TenantWrightTrustHeaders.OriginPayloadSha256)]
    [InlineData(TenantWrightTrustHeaders.OriginIssuedAt)]
    public async Task Missing_origin_header_should_be_rejected(string header)
    {
        using var fixture = new NeutralTrustFixture();
        var message = NeutralTrustFixture.HostMessage();
        var stamped = fixture.Stamper().Stamp(message, TenantWrightMessageRouteKind.Event);
        var headers = NeutralTrustFixture.WithHeaders(stamped);
        headers.Remove(header);

        await AssertRejectedAsync(fixture.Verifier(), NeutralTrustFixture.Delivery(stamped, message, headers: headers),
            MessagingSecurityException.InvalidContext);
    }

    [Fact]
    public async Task Missing_message_id_should_be_rejected()
    {
        using var fixture = new NeutralTrustFixture();
        var message = NeutralTrustFixture.HostMessage();
        var stamped = fixture.Stamper().Stamp(message, TenantWrightMessageRouteKind.Event);
        var delivery = NeutralTrustFixture.Delivery(stamped, message) with { MessageId = null };

        await AssertRejectedAsync(fixture.Verifier(), delivery, MessagingSecurityException.InvalidContext);
    }

    [Fact]
    public async Task Delivery_on_an_endpoint_that_does_not_bind_the_contract_should_be_rejected()
    {
        using var fixture = new NeutralTrustFixture();
        var message = NeutralTrustFixture.HostMessage();
        var stamped = fixture.Stamper().Stamp(message, TenantWrightMessageRouteKind.Event);
        var delivery = NeutralTrustFixture.Delivery(stamped, message) with { EndpointName = "tenantwright-unknown-endpoint" };

        await AssertRejectedAsync(fixture.Verifier(), delivery, MessagingSecurityException.UntrustedCustody);
    }

    [Fact]
    public async Task Replay_evidence_without_a_registered_replay_verifier_should_be_rejected()
    {
        using var fixture = new NeutralTrustFixture();
        var (delivery, _) = await CreateReplayDeliveryAsync(fixture);

        await AssertRejectedAsync(fixture.Verifier(), delivery, MessagingSecurityException.ReplayRejected);
        Assert.Equal(0, fixture.ReplayIds.ConsumedCount);
    }

    [Fact]
    public async Task Valid_replay_should_be_accepted_once_when_the_provider_registered_a_replay_verifier()
    {
        using var fixture = new NeutralTrustFixture();
        var (delivery, replay) = await CreateReplayDeliveryAsync(fixture);
        var replayVerifier = new RecordingReplayCustodyVerifier();

        var verified = await fixture.Verifier(replay: replayVerifier).VerifyAsync(delivery, default);

        Assert.Equal(TenantWrightInboundCustody.Replayed, verified.Custody);
        Assert.Equal(replay.ReplayId, Assert.Single(replayVerifier.Verified).ReplayId);
        Assert.Equal(1, fixture.ReplayIds.ConsumedCount);
        await AssertRejectedAsync(fixture.Verifier(replay: replayVerifier), delivery, MessagingSecurityException.ReplayRejected);
    }

    [Fact]
    public async Task Replay_headers_under_the_producer_identity_should_be_rejected()
    {
        using var fixture = new NeutralTrustFixture();
        var (delivery, _) = await CreateReplayDeliveryAsync(fixture);

        await AssertRejectedAsync(
            fixture.Verifier(replay: new RecordingReplayCustodyVerifier()),
            delivery with { BrokerActor = NeutralTrustFixture.ProducerPrincipal },
            MessagingSecurityException.UntrustedProducer);
    }

    [Fact]
    public async Task Replay_bound_to_another_destination_should_be_rejected()
    {
        using var fixture = new NeutralTrustFixture();
        var (delivery, _) = await CreateReplayDeliveryAsync(fixture, destination: "tenantwright-other-endpoint-v1");

        await AssertRejectedAsync(
            fixture.Verifier(replay: new RecordingReplayCustodyVerifier()),
            delivery,
            MessagingSecurityException.ReplayRejected);
    }

    private static async Task<(TenantWrightInboundMessage Delivery, ReplayAttestation Replay)> CreateReplayDeliveryAsync(
        NeutralTrustFixture fixture,
        string destination = NeutralTrustFixture.Endpoint)
    {
        var message = NeutralTrustFixture.HostMessage();
        var stamped = fixture.Stamper().Stamp(message, TenantWrightMessageRouteKind.Event);
        var replayId = Guid.NewGuid().ToString("D");
        var expiresAt = fixture.Now.AddMinutes(10);
        Assert.True(await fixture.ReplayIds.TryIssueAsync(
            new ProtectedReplayIssue(replayId, destination, "audit-1", expiresAt), default));
        var replay = fixture.Replay.CreateReplay(
            new string('a', 64),
            TenantWrightAttestationService.SignatureDigest(stamped.Origin.Signature),
            stamped.MessageId,
            stamped.Origin.PayloadSha256,
            destination,
            replayId,
            "audit-1",
            fixture.Now.AddSeconds(-1),
            expiresAt);
        var delivery = NeutralTrustFixture.Delivery(
            stamped,
            message,
            brokerActor: fixture.Options.Value.Replay.Actor,
            headers: NeutralTrustFixture.WithHeaders(stamped, TenantWrightTrustHeaderMap.FromReplay(replay)));
        return (delivery, replay);
    }

    private static async Task AssertRejectedAsync(
        TenantWrightInboundTrustVerifier verifier,
        TenantWrightInboundMessage delivery,
        string expectedReason)
    {
        var exception = await Assert.ThrowsAsync<MessagingSecurityException>(() => verifier.VerifyAsync(delivery, default));
        Assert.Equal(expectedReason, exception.Message);
    }
}
