using RabbitMQ.Client;
using Shared.EventBus.MassTransit;
using Shared.EventBus.Messaging;
using Xunit;

namespace Shared.EventBus.Tests;

public sealed class MessagingAttestationTests
{
    [Fact]
    public void Origin_relay_terminal_and_replay_attestations_should_reject_field_tampering()
    {
        using var signer = new MessagingTestSigner(MessagingTestData.IdentityPrincipal, "identity-key-1");
        var attestations = new TenantWrightAttestationService(signer, new MessagingTestPublicKeyRegistry(signer));
        var now = new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);
        var messageId = Guid.NewGuid();
        var payloadDigest = TenantWrightAttestationService.Sha256("payload"u8);
        var origin = attestations.CreateOrigin(messageId, "Shared.EventContracts.TenantDeactivatedEto", "tenant",
            Guid.NewGuid().ToString("D"), now, payloadDigest);
        var relay = attestations.CreateRelay(messageId, payloadDigest,
            TenantWrightAttestationService.SignatureDigest(origin.Signature), MessagingTestData.WorkflowsEndpoint,
            Guid.NewGuid().ToString("D"), 1, now.AddSeconds(30), now.AddMinutes(10));
        var terminal = attestations.CreateTerminal(
            TenantWrightAttestationService.Sha256("raw"u8),
            TenantWrightAttestationService.SignatureDigest(origin.Signature),
            TenantWrightAttestationService.SignatureDigest(relay.Signature),
            MessagingTestData.WorkflowsEndpoint,
            Guid.NewGuid().ToString("D"),
            "fault",
            now);
        var replay = attestations.CreateReplay(
            TenantWrightAttestationService.SignatureDigest(terminal.Signature),
            TenantWrightAttestationService.SignatureDigest(relay.Signature),
            messageId,
            payloadDigest,
            MessagingTestData.WorkflowsEndpoint,
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            now,
            now.AddMinutes(10));

        Assert.True(attestations.Verify(origin));
        Assert.True(attestations.Verify(relay));
        Assert.True(attestations.Verify(terminal));
        Assert.True(attestations.Verify(replay));
        Assert.False(attestations.Verify(origin with { PayloadSha256 = TenantWrightAttestationService.Sha256("changed"u8) }));
        Assert.False(attestations.Verify(relay with { Attempt = 2 }));
        Assert.False(attestations.Verify(terminal with { Classification = "skipped" }));
        Assert.False(attestations.Verify(replay with { Destination = "tenantwright-other" }));
    }

    [Fact]
    public void Revoked_or_wrong_actor_key_should_fail_verification()
    {
        using var signer = new MessagingTestSigner(MessagingTestData.IdentityPrincipal, "identity-key-1");
        using var wrongSigner = new MessagingTestSigner("tenantwright-attacker", "attacker-key-1");
        var creator = new TenantWrightAttestationService(signer, new MessagingTestPublicKeyRegistry(signer));
        var verifier = new TenantWrightAttestationService(wrongSigner, new MessagingTestPublicKeyRegistry(wrongSigner));
        var origin = creator.CreateOrigin(Guid.NewGuid(), "Shared.EventContracts.TenantDeactivatedEto", "tenant",
            Guid.NewGuid().ToString("D"), DateTimeOffset.UtcNow, TenantWrightAttestationService.Sha256("payload"u8));

        Assert.False(verifier.Verify(origin));
    }

    [Fact]
    public void Exact_bounded_death_history_should_be_accepted()
    {
        var now = new DateTimeOffset(2026, 7, 25, 12, 5, 0, TimeSpan.Zero);
        var notBefore = now.AddMinutes(-1);
        var records = new[]
        {
            new RabbitDeathRecord("mt-delay-30000", "mt-delay-30000", "expired", 1, now.AddMinutes(-4)),
            new RabbitDeathRecord("mt-delay-120000", "mt-delay-120000", "expired", 1, notBefore)
        };

        var exception = Record.Exception(() => RabbitDeathHistory.Verify(
            records, new[] { 30, 120, 600 }, expectedAttempt: 2, maximumRecords: 8,
            notBefore, now, TimeSpan.FromMinutes(2)));

        Assert.Null(exception);
    }

    [Fact]
    public void Duplicate_or_incomplete_death_history_should_fail_closed()
    {
        var now = DateTimeOffset.UtcNow;
        var records = new[]
        {
            new RabbitDeathRecord("mt-delay-30000", "mt-delay-30000", "expired", 1, now.AddMinutes(-2)),
            new RabbitDeathRecord("mt-delay-30000", "mt-delay-30000", "expired", 1, now.AddMinutes(-1))
        };

        var exception = Assert.Throws<MessagingSecurityException>(() =>
            RabbitDeathHistory.Verify(
                records, new[] { 30, 120, 600 }, 2, 8, now.AddMinutes(-1), now, TimeSpan.FromMinutes(2)));

        Assert.Equal(MessagingSecurityException.UntrustedCustody, exception.Message);
    }

    [Fact]
    public void Rabbit_death_parser_should_accept_only_bounded_typed_records()
    {
        var timestamp = DateTimeOffset.UtcNow;
        IDictionary<string, object?> headers = new Dictionary<string, object?>
        {
            ["x-death"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["queue"] = "mt-delay-30000"u8.ToArray(),
                    ["exchange"] = "mt-delay-30000"u8.ToArray(),
                    ["reason"] = "expired"u8.ToArray(),
                    ["count"] = 1L,
                    ["time"] = new AmqpTimestamp(timestamp.ToUnixTimeSeconds())
                }
            }
        };

        var records = RabbitDeathHistory.Read(headers, maximumRecords: 1);

        var record = Assert.Single(records);
        Assert.Equal("mt-delay-30000", record.Queue);
        Assert.Equal(timestamp.ToUnixTimeSeconds(), record.Time?.ToUnixTimeSeconds());
        Assert.Throws<MessagingSecurityException>(() => RabbitDeathHistory.Read(headers, maximumRecords: 0));
    }
}
