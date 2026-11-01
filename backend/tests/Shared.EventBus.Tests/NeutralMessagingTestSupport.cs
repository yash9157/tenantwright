using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shared.EventBus.Messaging;
using Shared.EventContracts;

namespace Shared.EventBus.Tests;

/// <summary>Transport evidence with a controllable broker redelivery history.</summary>
internal sealed class TestTransportEvidence(bool hasRedeliveryHistory) : TenantWrightTransportEvidence
{
    public override bool HasRedeliveryHistory { get; } = hasRedeliveryHistory;
}

internal sealed class RecordingRedeliveryCustodyVerifier : ITenantWrightRedeliveryCustodyVerifier
{
    public List<RelayAttestation> Verified { get; } = [];

    public void VerifyRedeliveryHistory(TenantWrightInboundMessage message, RelayAttestation relay, DateTimeOffset now) =>
        Verified.Add(relay);
}

internal sealed class RecordingReplayCustodyVerifier : ITenantWrightReplayCustodyVerifier
{
    public List<ReplayAttestation> Verified { get; } = [];

    public void VerifyReplayDelivery(TenantWrightInboundMessage message, ReplayAttestation replay, DateTimeOffset now) =>
        Verified.Add(replay);
}

/// <summary>Replay IDs issued up front; consumption succeeds exactly once per matching issue.</summary>
internal sealed class InMemoryReplayIdempotencyStore : IProtectedReplayIdempotencyStore
{
    private readonly ConcurrentDictionary<string, ProtectedReplayIssue> _issued = new(StringComparer.Ordinal);

    public int ConsumedCount { get; private set; }

    public Task<bool> TryIssueAsync(ProtectedReplayIssue issue, CancellationToken cancellationToken) =>
        Task.FromResult(_issued.TryAdd(issue.ReplayId, issue));

    public Task<bool> TryConsumeIssuedAsync(ProtectedReplayConsumption consumption, CancellationToken cancellationToken)
    {
        if (!_issued.TryGetValue(consumption.ReplayId, out var issue) ||
            !string.Equals(issue.Destination, consumption.Destination, StringComparison.Ordinal) ||
            !string.Equals(issue.AuditId, consumption.AuditId, StringComparison.Ordinal) ||
            issue.ExpiresAt != consumption.ExpiresAt ||
            consumption.ConsumedAt >= issue.ExpiresAt ||
            !_issued.TryRemove(consumption.ReplayId, out _))
            return Task.FromResult(false);
        ConsumedCount++;
        return Task.FromResult(true);
    }
}

/// <summary>
/// An in-memory inbox with the production contract: the first delivery of a message to an endpoint runs the handler,
/// later ones are acknowledged without it, and a handler failure leaves the message unrecorded.
/// </summary>
internal sealed class InMemoryInboxTransaction(IServiceProvider services) : ITenantWrightInboxTransaction
{
    private readonly ConcurrentDictionary<(Guid, string), byte> _processed = new();

    public int AttemptCount;

    public async Task<bool> TryProcessOnceAsync(
        Guid messageId,
        string endpoint,
        Func<IServiceProvider, CancellationToken, Task> handle,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref AttemptCount);
        if (!_processed.TryAdd((messageId, endpoint), 0))
            return false;
        try
        {
            await using var scope = services.CreateAsyncScope();
            await handle(scope.ServiceProvider, cancellationToken);
            return true;
        }
        catch
        {
            _processed.TryRemove((messageId, endpoint), out _);
            throw;
        }
    }
}

/// <summary>
/// Real signing, catalog, stamping and verification for the neutral trust tests. The producer is
/// <c>tenantwright-identity</c>; the receive endpoint is owned by <c>tenantwright-workflows</c>.
/// </summary>
internal sealed class NeutralTrustFixture : IDisposable
{
    public const string ProducerPrincipal = "tenantwright-identity";
    public const string EndpointOwner = "tenantwright-workflows";
    public const string UntrustedPrincipal = "tenantwright-tenant";
    public const string Endpoint = "tenantwright-workflows-neutral-test-v1";

    public NeutralTrustFixture(DateTimeOffset? now = null)
    {
        Now = now ?? DateTimeOffset.Parse("2026-07-25T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        Time = new FixedMessagingTimeProvider(Now);
        ProducerSigner = new MessagingTestSigner(ProducerPrincipal, "identity-key");
        OwnerSigner = new MessagingTestSigner(EndpointOwner, "workflows-key");
        UntrustedSigner = new MessagingTestSigner(UntrustedPrincipal, "tenant-key");
        ReplaySigner = new MessagingTestSigner(Options.Value.Replay.Actor, "replay-key");
        Keys = new MessagingTestPublicKeyRegistry(ProducerSigner, OwnerSigner, UntrustedSigner, ReplaySigner);
        Producer = new TenantWrightAttestationService(ProducerSigner, Keys);
        Owner = new TenantWrightAttestationService(OwnerSigner, Keys);
        Untrusted = new TenantWrightAttestationService(UntrustedSigner, Keys);
        Replay = new TenantWrightAttestationService(ReplaySigner, Keys);

        Catalog = new TenantWrightMessageCatalog(
            [
                new TenantWrightContractRoute(typeof(TenantDeactivatedEto), TenantWrightMessageRouteKind.Event, null,
                    new HashSet<string>(StringComparer.Ordinal) { ProducerPrincipal }),
                new TenantWrightContractRoute(typeof(PermissionGrantsChangedTenantEto), TenantWrightMessageRouteKind.Event, null,
                    new HashSet<string>(StringComparer.Ordinal) { ProducerPrincipal })
            ],
            [
                new TenantWrightEndpointRoute(Endpoint, EndpointOwner,
                    [typeof(TenantDeactivatedEto), typeof(PermissionGrantsChangedTenantEto)])
            ],
            [
                new TenantWrightReceiveEndpoint(Endpoint, EndpointOwner,
                    [typeof(TenantDeactivatedEto), typeof(PermissionGrantsChangedTenantEto)])
            ]);
    }

    public DateTimeOffset Now { get; }
    public TimeProvider Time { get; }
    public IOptions<MessagingOptions> Options { get; } = Microsoft.Extensions.Options.Options.Create(new MessagingOptions());
    public MessagingTestSigner ProducerSigner { get; }
    public MessagingTestSigner OwnerSigner { get; }
    public MessagingTestSigner UntrustedSigner { get; }
    public MessagingTestSigner ReplaySigner { get; }
    public MessagingTestPublicKeyRegistry Keys { get; }
    public TenantWrightAttestationService Producer { get; }
    public TenantWrightAttestationService Owner { get; }
    public TenantWrightAttestationService Untrusted { get; }
    public TenantWrightAttestationService Replay { get; }
    public TenantWrightMessageCatalog Catalog { get; }
    public InMemoryReplayIdempotencyStore ReplayIds { get; } = new();

    public TenantWrightOutboundMessageStamper Stamper(MessagingTestCurrentTenant? currentTenant = null, TimeProvider? time = null) =>
        new(Catalog, Producer, currentTenant ?? new MessagingTestCurrentTenant(), time ?? Time);

    public TenantWrightInboundTrustVerifier Verifier(
        ITenantWrightRedeliveryCustodyVerifier? redelivery = null,
        ITenantWrightReplayCustodyVerifier? replay = null) =>
        new(Catalog, Producer, ReplayIds, Options, Time,
            redelivery is null ? [] : [redelivery],
            replay is null ? [] : [replay]);

    public static TenantDeactivatedEto HostMessage() => new()
    {
        TenantId = Guid.Parse("00000000-0000-0000-0000-000000000123"),
        TenantName = "Neutral Test Tenant",
        OccurredAtUtc = new DateTime(2026, 7, 25, 11, 59, 0, DateTimeKind.Utc)
    };

    public static PermissionGrantsChangedTenantEto TenantMessage(Guid tenantId) => new()
    {
        TenantId = tenantId,
        ProviderName = "neutral-test-provider",
        ProviderKey = "neutral-test-key",
        Stamp = 7,
        OccurredAtUtc = new DateTimeOffset(2026, 7, 25, 11, 59, 0, TimeSpan.Zero)
    };

    /// <summary>The inbound delivery a direct send of <paramref name="stamped"/> produces.</summary>
    public static TenantWrightInboundMessage Delivery(
        TenantWrightOutboundMessage stamped,
        object message,
        string? brokerActor = ProducerPrincipal,
        IReadOnlyDictionary<string, string?>? headers = null,
        TenantWrightTransportEvidence? evidence = null) =>
        new(Endpoint,
            stamped.MessageId,
            message,
            headers ?? stamped.Headers.ToDictionary(header => header.Key, string? (header) => header.Value, StringComparer.Ordinal),
            brokerActor,
            1,
            evidence ?? NoTenantWrightTransportEvidence.Instance);

    /// <summary>The endpoint owner's signed relay for <paramref name="stamped"/>.</summary>
    public RelayAttestation Relay(TenantWrightOutboundMessage stamped, DateTimeOffset notBefore, DateTimeOffset expiresAt, int attempt = 1) =>
        Owner.CreateRelay(
            stamped.MessageId,
            stamped.Origin.PayloadSha256,
            TenantWrightAttestationService.SignatureDigest(stamped.Origin.Signature),
            Endpoint,
            Guid.NewGuid().ToString("D"),
            attempt,
            notBefore,
            expiresAt);

    public static Dictionary<string, string?> WithHeaders(
        TenantWrightOutboundMessage stamped,
        params IReadOnlyDictionary<string, string>[] additional)
    {
        var headers = stamped.Headers.ToDictionary(header => header.Key, string? (header) => header.Value, StringComparer.Ordinal);
        foreach (var set in additional)
        {
            foreach (var header in set)
                headers[header.Key] = header.Value;
        }
        return headers;
    }

    public TenantWrightInboundMessagePipeline Pipeline(
        MessagingTestCurrentTenant currentTenant,
        ITenantWrightInboxTransaction inbox,
        ITenantWrightRedeliveryCustodyVerifier? redelivery = null) =>
        new(Verifier(redelivery), currentTenant, inbox, NullLogger<TenantWrightInboundMessagePipeline>.Instance);

    public void Dispose()
    {
        ProducerSigner.Dispose();
        OwnerSigner.Dispose();
        UntrustedSigner.Dispose();
        ReplaySigner.Dispose();
    }
}
