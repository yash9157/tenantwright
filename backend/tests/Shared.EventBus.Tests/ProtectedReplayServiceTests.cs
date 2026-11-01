using Microsoft.Extensions.Options;
using Shared.EventBus.Messaging;
using Shared.EventContracts;
using Xunit;

namespace Shared.EventBus.Tests;

public sealed class ProtectedReplayServiceTests
{
    [Fact]
    public async Task Authorized_replay_should_audit_reserve_and_dispatch_in_order()
    {
        using var fixture = new ReplayFixture();

        await fixture.Service.ReplayAsync(fixture.Request);

        Assert.Equal(new[] { "authorize", "audit", "issue", "send" }, fixture.Sequence);
        var dispatch = Assert.IsType<ProtectedReplayDispatch>(fixture.Transport.Dispatch);
        Assert.Equal(fixture.Request.ReplayId, dispatch.Attestation.ReplayId);
        Assert.Equal(fixture.Request.Destination, dispatch.Attestation.Destination);
        Assert.Equal(fixture.Request.AuditId, dispatch.Attestation.AuditId);
        Assert.Equal(TenantWrightName.ReplayPrincipal, dispatch.Attestation.Actor);
        Assert.True(fixture.ReplayAttestations.Verify(dispatch.Attestation));
    }

    [Fact]
    public async Task Invalid_terminal_provenance_should_fail_before_authorization_or_audit()
    {
        using var fixture = new ReplayFixture(provenanceVerified: false);

        var exception = await Assert.ThrowsAsync<MessagingSecurityException>(() =>
            fixture.Service.ReplayAsync(fixture.Request));

        Assert.Equal(MessagingSecurityException.ReplayRejected, exception.Message);
        Assert.Empty(fixture.Sequence);
        Assert.Null(fixture.Transport.Dispatch);
    }

    [Fact]
    public async Task Duplicate_replay_id_should_fail_closed_without_transport_dispatch()
    {
        using var fixture = new ReplayFixture(allowIssue: false);

        var exception = await Assert.ThrowsAsync<MessagingSecurityException>(() =>
            fixture.Service.ReplayAsync(fixture.Request));

        Assert.Equal(MessagingSecurityException.ReplayRejected, exception.Message);
        Assert.Equal(new[] { "authorize", "audit", "issue" }, fixture.Sequence);
        Assert.Null(fixture.Transport.Dispatch);
    }

    [Fact]
    public async Task Ambiguous_transport_failure_should_leave_replay_id_reserved()
    {
        using var fixture = new ReplayFixture(throwDuringTransport: true);

        await Assert.ThrowsAsync<ReplayTransportTestException>(() =>
            fixture.Service.ReplayAsync(fixture.Request));
        fixture.Transport.ThrowDuringSend = false;
        var replayException = await Assert.ThrowsAsync<MessagingSecurityException>(() =>
            fixture.Service.ReplayAsync(fixture.Request));

        Assert.Equal(MessagingSecurityException.ReplayRejected, replayException.Message);
        Assert.Equal(1, fixture.Transport.SendCount);
        Assert.True(fixture.ReplayIds.IsIssued(fixture.Request.ReplayId));
    }

    [Fact]
    public async Task Replay_destination_should_be_bound_to_terminal_and_catalog()
    {
        using var fixture = new ReplayFixture();
        var request = fixture.Request with { Destination = MessagingTestData.NotificationsEndpoint };

        var exception = await Assert.ThrowsAsync<MessagingSecurityException>(() =>
            fixture.Service.ReplayAsync(request));

        Assert.Equal(MessagingSecurityException.ReplayRejected, exception.Message);
        Assert.Empty(fixture.Sequence);
    }

    private sealed class ReplayFixture : IDisposable
    {
        private readonly MessagingTestSigner _originSigner =
            new(MessagingTestData.IdentityPrincipal, "identity-key-1");
        private readonly MessagingTestSigner _replaySigner =
            new(TenantWrightName.ReplayPrincipal, "replay-key-1");

        public ReplayFixture(
            bool provenanceVerified = true,
            bool allowIssue = true,
            bool throwDuringTransport = false)
        {
            Now = new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);
            Sequence = [];
            var registry = new MessagingTestPublicKeyRegistry(_originSigner, _replaySigner);
            var originAttestations = new TenantWrightAttestationService(_originSigner, registry);
            ReplayAttestations = new TenantWrightAttestationService(_replaySigner, registry);
            var message = new TenantDeactivatedEto
            {
                TenantId = Guid.Parse("00000000-0000-0000-0000-000000000102"),
                TenantName = "Test Tenant",
                OccurredAtUtc = new DateTime(2026, 7, 25, 12, 0, 0, DateTimeKind.Utc)
            };
            var payloadDigest = TenantWrightAttestationService.Sha256(
                TenantWrightContractSerializer.SerializeCanonical(message, typeof(TenantDeactivatedEto)));
            var messageId = Guid.Parse("00000000-0000-0000-0000-000000000106");
            var origin = originAttestations.CreateOrigin(
                messageId,
                "Shared.EventContracts.TenantDeactivatedEto",
                "tenant",
                message.TenantId.ToString("D"),
                Now.AddMinutes(-1),
                payloadDigest);
            var originalChainDigest = TenantWrightAttestationService.SignatureDigest(origin.Signature);
            var terminalId = "terminal-00000000-0000-0000-0000-000000000107";
            var summary = new ProtectedTerminalSummary(
                terminalId,
                MessagingTestData.WorkflowsEndpoint,
                "fault",
                Now.AddMinutes(-1),
                Now.AddDays(1),
                provenanceVerified,
                "Shared.EventContracts.TenantDeactivatedEto");
            var material = new ProtectedReplayMaterial(
                summary,
                messageId,
                payloadDigest,
                TenantWrightAttestationService.Sha256("terminal-record"u8),
                originalChainDigest,
                typeof(TenantDeactivatedEto),
                message,
                origin,
                null);

            Request = new ProtectedReplayRequest(
                terminalId,
                MessagingTestData.WorkflowsEndpoint,
                "operator approved recovery",
                "authorization-1",
                "audit-1",
                "replay-1");
            ReplayIds = new ReplayIdStore(Sequence, allowIssue);
            Transport = new ReplayTransport(Sequence) { ThrowDuringSend = throwDuringTransport };
            var options = Microsoft.Extensions.Options.Options.Create(new MessagingOptions
            {
                Provider = TenantWrightMessagingProvider.MassTransit,
                ServiceName = TenantWrightName.ReplayPrincipal,
                ProducerPrincipal = TenantWrightName.ReplayPrincipal,
                Replay = new MessagingReplayOptions
                {
                    Actor = TenantWrightName.ReplayPrincipal,
                    MaximumAuthorizationLifetime = TimeSpan.FromMinutes(15)
                }
            });
            Service = new ProtectedReplayService(
                new TerminalStore(material),
                new ReplayAuthorization(Sequence),
                new ReplayAudit(Sequence),
                ReplayIds,
                Transport,
                MessagingTestData.CreateCatalog(),
                ReplayAttestations,
                options,
                new FixedMessagingTimeProvider(Now));
        }

        public DateTimeOffset Now { get; }
        public List<string> Sequence { get; }
        public ProtectedReplayRequest Request { get; }
        public ReplayIdStore ReplayIds { get; }
        public ReplayTransport Transport { get; }
        public TenantWrightAttestationService ReplayAttestations { get; }
        public ProtectedReplayService Service { get; }

        public void Dispose()
        {
            _originSigner.Dispose();
            _replaySigner.Dispose();
        }
    }

    private sealed class TerminalStore(ProtectedReplayMaterial material) : IProtectedTerminalStore
    {
        public Task<ProtectedReplayMaterial?> FindAsync(string terminalId, CancellationToken cancellationToken) =>
            Task.FromResult<ProtectedReplayMaterial?>(
                string.Equals(terminalId, material.Summary.TerminalId, StringComparison.Ordinal) ? material : null);
    }

    private sealed class ReplayAuthorization(List<string> sequence) : IProtectedReplayAuthorization
    {
        public Task AuthorizeAsync(
            ProtectedReplayRequest request,
            ProtectedTerminalSummary terminal,
            CancellationToken cancellationToken)
        {
            sequence.Add("authorize");
            return Task.CompletedTask;
        }
    }

    private sealed class ReplayAudit(List<string> sequence) : IProtectedReplayAudit
    {
        public Task RecordAsync(
            ProtectedReplayRequest request,
            ProtectedTerminalSummary terminal,
            CancellationToken cancellationToken)
        {
            sequence.Add("audit");
            return Task.CompletedTask;
        }
    }

    internal sealed class ReplayIdStore(List<string> sequence, bool allowIssue) : IProtectedReplayIdempotencyStore
    {
        private readonly HashSet<string> _issued = new(StringComparer.Ordinal);

        public Task<bool> TryIssueAsync(ProtectedReplayIssue issue, CancellationToken cancellationToken)
        {
            sequence.Add("issue");
            return Task.FromResult(allowIssue && _issued.Add(issue.ReplayId));
        }

        public Task<bool> TryConsumeIssuedAsync(
            ProtectedReplayConsumption consumption,
            CancellationToken cancellationToken) =>
            Task.FromResult(_issued.Remove(consumption.ReplayId));

        public bool IsIssued(string replayId) => _issued.Contains(replayId);
    }

    internal sealed class ReplayTransport(List<string> sequence) : IProtectedReplayTransport
    {
        public bool ThrowDuringSend { get; set; }
        public int SendCount { get; private set; }
        public ProtectedReplayDispatch? Dispatch { get; private set; }

        public Task SendAsync(ProtectedReplayDispatch dispatch, CancellationToken cancellationToken)
        {
            sequence.Add("send");
            SendCount++;
            Dispatch = dispatch;
            if (ThrowDuringSend)
                throw new ReplayTransportTestException();
            return Task.CompletedTask;
        }
    }

    private sealed class ReplayTransportTestException : Exception;
}
