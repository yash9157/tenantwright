using Shared.EventBus.Messaging;
using Shared.EventContracts;
using Xunit;

namespace Shared.EventBus.Tests;

public sealed class TenantWrightOutboundMessageStamperTests
{
    [Fact]
    public void Host_only_contract_should_stamp_host_scope_without_a_tenant()
    {
        using var fixture = new NeutralTrustFixture();
        var message = NeutralTrustFixture.HostMessage();

        var stamped = fixture.Stamper().Stamp(message, TenantWrightMessageRouteKind.Event);

        Assert.Equal("Shared.EventContracts.TenantDeactivatedEto", stamped.ContractIdentity);
        Assert.Equal(TenantWrightMessageRouteKind.Event, stamped.Kind);
        Assert.Null(stamped.Destination);
        Assert.Null(stamped.TenantId);
        Assert.Equal("host", stamped.Origin.Scope);
        Assert.Null(stamped.Origin.TenantId);
        Assert.False(stamped.Headers.ContainsKey(TenantWrightTrustHeaders.TenantId));
        Assert.True(fixture.Producer.Verify(stamped.Origin));
    }

    [Fact]
    public void Host_only_contract_should_be_refused_under_an_ambient_tenant()
    {
        using var fixture = new NeutralTrustFixture();
        var currentTenant = new MessagingTestCurrentTenant(Guid.NewGuid());

        var exception = Assert.Throws<MessagingSecurityException>(() =>
            fixture.Stamper(currentTenant).Stamp(NeutralTrustFixture.HostMessage(), TenantWrightMessageRouteKind.Event));

        Assert.Equal(MessagingSecurityException.TenantMismatch, exception.Message);
    }

    [Fact]
    public void Tenant_contract_should_bind_the_exact_ambient_tenant()
    {
        using var fixture = new NeutralTrustFixture();
        var tenantId = Guid.NewGuid();
        var currentTenant = new MessagingTestCurrentTenant(tenantId);

        var stamped = fixture.Stamper(currentTenant)
            .Stamp(NeutralTrustFixture.TenantMessage(tenantId), TenantWrightMessageRouteKind.Event);

        Assert.Equal(tenantId, stamped.TenantId);
        Assert.Equal("tenant", stamped.Origin.Scope);
        Assert.Equal(tenantId.ToString("D"), stamped.Origin.TenantId);
        Assert.Equal(tenantId.ToString("D"), stamped.Headers[TenantWrightTrustHeaders.TenantId]);
    }

    [Fact]
    public void Tenant_contract_should_be_refused_without_an_ambient_tenant_or_with_a_different_body_tenant()
    {
        using var fixture = new NeutralTrustFixture();
        var tenantId = Guid.NewGuid();

        var missing = Assert.Throws<MessagingSecurityException>(() =>
            fixture.Stamper().Stamp(NeutralTrustFixture.TenantMessage(tenantId), TenantWrightMessageRouteKind.Event));
        var mismatched = Assert.Throws<MessagingSecurityException>(() =>
            fixture.Stamper(new MessagingTestCurrentTenant(Guid.NewGuid()))
                .Stamp(NeutralTrustFixture.TenantMessage(tenantId), TenantWrightMessageRouteKind.Event));

        Assert.Equal(MessagingSecurityException.TenantMismatch, missing.Message);
        Assert.Equal(MessagingSecurityException.TenantMismatch, mismatched.Message);
    }

    [Fact]
    public void Disallowed_producer_should_be_rejected_before_anything_is_signed()
    {
        using var fixture = new NeutralTrustFixture();
        var stamper = new TenantWrightOutboundMessageStamper(
            fixture.Catalog, fixture.Untrusted, new MessagingTestCurrentTenant(), fixture.Time);

        var exception = Assert.Throws<MessagingSecurityException>(() =>
            stamper.Stamp(NeutralTrustFixture.HostMessage(), TenantWrightMessageRouteKind.Event));

        Assert.Equal(MessagingSecurityException.UntrustedProducer, exception.Message);
    }

    [Fact]
    public void Route_kind_mismatch_and_unapproved_contracts_should_be_rejected()
    {
        using var fixture = new NeutralTrustFixture();

        var wrongKind = Assert.Throws<MessagingSecurityException>(() =>
            fixture.Stamper().Stamp(NeutralTrustFixture.HostMessage(), TenantWrightMessageRouteKind.Command));
        var unapproved = Assert.Throws<MessagingSecurityException>(() =>
            fixture.Stamper().Stamp(new TenantClosureChangedEto(), TenantWrightMessageRouteKind.Event));

        Assert.Equal(MessagingSecurityException.UnsupportedContract, wrongKind.Message);
        Assert.Equal(MessagingSecurityException.UnsupportedContract, unapproved.Message);
    }

    [Fact]
    public void Headers_should_carry_exactly_the_signed_origin_under_the_unchanged_wire_names()
    {
        using var fixture = new NeutralTrustFixture();
        var tenantId = Guid.NewGuid();
        var message = NeutralTrustFixture.TenantMessage(tenantId);

        var stamped = fixture.Stamper(new MessagingTestCurrentTenant(tenantId)).Stamp(message, TenantWrightMessageRouteKind.Event);

        Assert.Equal(
            new[]
            {
                "x-tenantwright-context-version", "x-tenantwright-origin-issued-at", "x-tenantwright-origin-key-id",
                "x-tenantwright-origin-payload-sha256", "x-tenantwright-origin-signature", "x-tenantwright-producer",
                "x-tenantwright-scope", "x-tenantwright-tenant-id"
            },
            stamped.Headers.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("1", stamped.Headers[TenantWrightTrustHeaders.ContextVersion]);
        Assert.Equal(NeutralTrustFixture.ProducerPrincipal, stamped.Headers[TenantWrightTrustHeaders.Producer]);
        Assert.Equal("identity-key", stamped.Headers[TenantWrightTrustHeaders.OriginKeyId]);
        Assert.Equal("2026-07-25T12:00:00.0000000Z", stamped.Headers[TenantWrightTrustHeaders.OriginIssuedAt]);
        Assert.Equal(
            TenantWrightAttestationService.Sha256(fixture.Catalog.SerializeCanonical(message, message.GetType())),
            stamped.Headers[TenantWrightTrustHeaders.OriginPayloadSha256]);
        Assert.Equal(stamped.Origin.Signature, stamped.Headers[TenantWrightTrustHeaders.OriginSignature]);
        Assert.Equal(stamped.MessageId, stamped.Origin.MessageId);
        Assert.Equal(7, stamped.MessageId.Version);
        Assert.Equal(
            fixture.Catalog.SerializeCanonical(message, message.GetType()),
            stamped.Payload);
    }
}
