using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using Shared.EventBus.Messaging;
using Shared.MultiTenancy;
using Wolverine;
using Xunit;

namespace Shared.EventBus.Wolverine.Tests;

public sealed class WolverineProtectedReplayTests : IDisposable
{
    internal const string ReplayPrincipal = WolverineTestTopology.Replay;
    private readonly MessagingTestKeys _keys = new([.. WolverineTestTopology.Principals]);
    private readonly ReplayClock _clock = new(DateTimeOffset.UtcNow);

    public void Dispose() => _keys.Dispose();

    [Fact]
    public async Task Signed_terminal_round_trips_to_a_fresh_endpoint_bound_replay()
    {
        await using var producer = Core(WolverineTestTopology.Producer);
        await using var consumer = Core(WolverineTestTopology.Consumer);
        await using var replay = Core(ReplayPrincipal);
        var (properties, body) = Terminal(producer, consumer);
        var material = Reader(replay).Read(WolverineTestTopology.CommandEndpoint, properties, body);
        var attestation = Replay(replay, material);

        var (publication, canonical) = WolverineProtectedReplayTransport.CreatePublication(
            new ProtectedReplayDispatch(material, attestation), WolverineTestTopology.ProducerCatalog(),
            replay.GetRequiredService<TenantWrightAttestationService>(),
            replay.GetRequiredService<IOptions<MessagingOptions>>().Value, _clock.GetUtcNow());

        Assert.Equal(body, canonical);
        Assert.Equal(ReplayPrincipal, publication.UserId);
        Assert.Equal(properties.MessageId, publication.MessageId);
        Assert.DoesNotContain("x-death", publication.Headers!.Keys);
        Assert.DoesNotContain(publication.Headers.Keys, TenantWrightTrustHeaders.Terminal.Contains);
        Assert.DoesNotContain(publication.Headers.Keys, TenantWrightTrustHeaders.Relay.Contains);
        Assert.Equal(attestation.Signature, publication.Headers[TenantWrightTrustHeaders.ReplaySignature]);
    }

    [Theory]
    [InlineData("raw-body")]
    [InlineData("terminal-signature")]
    [InlineData("origin-signature")]
    [InlineData("terminal-time")]
    [InlineData("terminal-retention")]
    [InlineData("origin-expiry")]
    [InlineData("native-death")]
    [InlineData("replay-evidence")]
    [InlineData("relay-evidence")]
    [InlineData("wrong-owner")]
    [InlineData("wrong-endpoint")]
    [InlineData("unsigned")]
    public async Task Terminal_reader_rejects_untrusted_or_expired_snapshots(string mutation)
    {
        await using var producer = Core(WolverineTestTopology.Producer);
        await using var consumer = Core(WolverineTestTopology.Consumer);
        var (properties, body) = Terminal(producer, consumer);
        var endpoint = WolverineTestTopology.CommandEndpoint;
        switch (mutation)
        {
            case "raw-body": body = "{}"u8.ToArray(); break;
            case "terminal-signature": properties.Headers![TenantWrightTrustHeaders.TerminalSignature] = "invalid"; break;
            case "origin-signature": properties.Headers![TenantWrightTrustHeaders.OriginSignature] = "invalid"; break;
            case "terminal-time": properties.Headers![TenantWrightTrustHeaders.TerminalTimestamp] = "invalid"; break;
            case "terminal-retention": _clock.Advance(TimeSpan.FromDays(31)); break;
            case "origin-expiry": _clock.Advance(TimeSpan.FromDays(2)); break;
            case "native-death": properties.Headers!["x-death"] = new List<object>(); break;
            case "replay-evidence": properties.Headers![TenantWrightTrustHeaders.ReplayId] = "old-authorization"; break;
            case "relay-evidence": properties.Headers![TenantWrightTrustHeaders.RelayId] = "unverified-relay"; break;
            case "wrong-owner": properties.UserId = WolverineTestTopology.Outsider; break;
            case "wrong-endpoint": endpoint = WolverineTestTopology.FirstEventEndpoint; break;
            case "unsigned": properties.Headers!.Remove(TenantWrightTrustHeaders.TerminalSignature); break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        Assert.Throws<MessagingSecurityException>(() => Reader(consumer).Read(endpoint, properties, body));
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("scope")]
    public async Task Valid_signatures_cannot_override_the_payload_tenant_or_contract_scope(string mutation)
    {
        await using var producer = Core(WolverineTestTopology.Producer);
        await using var consumer = Core(WolverineTestTopology.Consumer);
        var (properties, body) = Terminal(producer, consumer, mutation);

        Assert.Throws<MessagingSecurityException>(() => Reader(consumer).Read(WolverineTestTopology.CommandEndpoint, properties, body));
    }

    [Theory]
    [InlineData("origin")]
    [InlineData("terminal")]
    public async Task Revoked_signing_keys_prohibit_terminal_recovery(string key)
    {
        await using var producer = Core(WolverineTestTopology.Producer);
        await using var consumer = Core(WolverineTestTopology.Consumer);
        var (properties, body) = Terminal(producer, consumer);
        var settings = Settings(ReplayPrincipal);
        var revokedPrincipal = key == "origin" ? WolverineTestTopology.Producer : WolverineTestTopology.Consumer;
        var index = Array.IndexOf(WolverineTestTopology.Principals.ToArray(), revokedPrincipal);
        settings[$"Messaging:Trust:PublicKeys:{index}:Revoked"] = "true";
        await using var revoked = Core(ReplayPrincipal, settings);

        Assert.Throws<MessagingSecurityException>(() => Reader(revoked).Read(WolverineTestTopology.CommandEndpoint, properties, body));
    }

    [Theory]
    [InlineData("wrong-principal")]
    [InlineData("wrong-destination")]
    [InlineData("changed-payload")]
    [InlineData("wrong-type")]
    [InlineData("expired")]
    [InlineData("forged-signature")]
    public async Task Replay_transport_denies_invalid_material_before_publishing(string mutation)
    {
        await using var producer = Core(WolverineTestTopology.Producer);
        await using var consumer = Core(WolverineTestTopology.Consumer);
        await using var replay = Core(ReplayPrincipal);
        var (properties, body) = Terminal(producer, consumer);
        var material = Reader(replay).Read(WolverineTestTopology.CommandEndpoint, properties, body);
        var attestation = Replay(replay, material);
        if (mutation == "wrong-destination")
            attestation = Replay(replay, material, WolverineTestTopology.FirstEventEndpoint);
        if (mutation == "changed-payload")
            ((Shared.EventContracts.NotificationSendRequestedTenantEto)material.Message).RequestId = Guid.NewGuid();
        if (mutation == "wrong-type")
            material = material with { Message = new object() };
        if (mutation == "expired")
            _clock.Advance(TimeSpan.FromHours(1));
        if (mutation == "forged-signature")
            attestation = attestation with { Signature = "invalid" };
        var signingProvider = mutation == "wrong-principal" ? consumer : replay;

        Assert.Throws<MessagingSecurityException>(() => WolverineProtectedReplayTransport.CreatePublication(
            new ProtectedReplayDispatch(material, attestation), WolverineTestTopology.ProducerCatalog(),
            signingProvider.GetRequiredService<TenantWrightAttestationService>(),
            signingProvider.GetRequiredService<IOptions<MessagingOptions>>().Value, _clock.GetUtcNow()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Replay_custody_requires_mapper_evidence_without_broker_history(bool deathHistory)
    {
        var message = new TenantWrightInboundMessage(WolverineTestTopology.CommandEndpoint, Guid.NewGuid(), new object(),
            new Dictionary<string, string?>(), ReplayPrincipal, 1,
            deathHistory ? new WolverineRabbitMqTransportEvidence(true) : NoTenantWrightTransportEvidence.Instance);
        Assert.Throws<MessagingSecurityException>(() =>
            new WolverineReplayCustodyVerifier().VerifyReplayDelivery(message, null!, _clock.GetUtcNow()));
    }

    [Fact]
    public async Task Cancelled_replay_does_not_publish()
    {
        await using var producer = Core(WolverineTestTopology.Producer);
        await using var consumer = Core(WolverineTestTopology.Consumer);
        await using var replay = Core(ReplayPrincipal);
        var (properties, body) = Terminal(producer, consumer);
        var material = Reader(replay).Read(WolverineTestTopology.CommandEndpoint, properties, body);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replay.GetRequiredService<IProtectedReplayTransport>()
            .SendAsync(new ProtectedReplayDispatch(material, Replay(replay, material)), cancellation.Token));
    }

    private (BasicProperties Properties, byte[] Body) Terminal(IServiceProvider producer, IServiceProvider consumer, string? originMutation = null)
    {
        var tenantId = Guid.NewGuid();
        var message = WolverineProviderUnitTests.NewCommand(tenantId);
        using var tenant = producer.GetRequiredService<ICurrentTenant>().Change(tenantId);
        using var scope = producer.CreateScope();
        var stamped = scope.ServiceProvider.GetRequiredService<TenantWrightOutboundMessageStamper>()
            .Stamp(message, TenantWrightMessageRouteKind.Command);
        var origin = stamped.Origin;
        if (originMutation is not null)
            origin = producer.GetRequiredService<TenantWrightAttestationService>().CreateOrigin(
                origin.MessageId, origin.ContractIdentity, originMutation == "scope" ? "host" : origin.Scope,
                originMutation == "tenant" ? Guid.NewGuid().ToString("D") : origin.TenantId, origin.IssuedAt, origin.PayloadSha256);
        var envelope = new Envelope { Data = stamped.Payload.ToArray(), Message = message };
        var delivery = new TenantWrightRabbitMqDelivery(WolverineTestTopology.CommandEndpoint, stamped.MessageId,
            WolverineTestTopology.Producer, TenantWrightTrustHeaderMap.FromOrigin(origin).ToDictionary(pair => pair.Key, string? (pair) => pair.Value),
            new WolverineRabbitMqTransportEvidence(false));
        return (WolverineTerminalPublisher.CreateProperties(envelope, delivery,
            consumer.GetRequiredService<TenantWrightAttestationService>(), _clock.GetUtcNow()), stamped.Payload.ToArray());
    }

    private ReplayAttestation Replay(IServiceProvider provider, ProtectedReplayMaterial material, string? destination = null) =>
        provider.GetRequiredService<TenantWrightAttestationService>().CreateReplay(
            material.TerminalRecordSha256, material.OriginalChainSha256, material.MessageId, material.PayloadSha256,
            destination ?? material.Summary.Endpoint, Guid.NewGuid().ToString("D"), "test-audit",
            _clock.GetUtcNow(), _clock.GetUtcNow().AddMinutes(10));

    private Dictionary<string, string?> Settings(string principal) => WolverineTestConfiguration.Messaging(
        new WolverineBrokerTarget("127.0.0.1", 5672, "tenantwright-replay-unit", "unused"),
        _keys, principal, WolverineTestTopology.Principals);

    private ServiceProvider Core(string principal, Dictionary<string, string?>? settings = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(Environments.Development));
        services.AddSingleton<TimeProvider>(_clock);
        services.AddSingleton<ICurrentTenant, TestAmbientTenant>();
        var configuration = WolverineTestConfiguration.Build(settings ?? Settings(principal));
        var catalog = WolverineTestTopology.ProducerCatalog();
        services.AddTenantWrightMessagingCore(configuration, catalog);
        new WolverineMessagingProvider().Register(services, configuration, catalog);
        return services.BuildServiceProvider();
    }

    private static WolverineTerminalMessageReader Reader(IServiceProvider provider) =>
        provider.GetRequiredService<WolverineTerminalMessageReader>();

    private sealed class ReplayClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }
}
