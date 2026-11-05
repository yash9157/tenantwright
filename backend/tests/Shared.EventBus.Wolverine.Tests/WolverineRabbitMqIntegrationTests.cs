using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using Shared.EntityFrameworkCore.Messaging;
using Shared.EventBus.Messaging;
using Shared.EventContracts;
using Xunit;

namespace Shared.EventBus.Wolverine.Tests;

/// <summary>
/// End-to-end proofs of the Wolverine provider against a real RabbitMQ 4 broker and PostgreSQL: two in-process hosts
/// with different principals and per-principal broker users, real signing keys, the neutral outbox and inbox, and the
/// chart's permission model. Not opt-in: these run whenever the suite runs and need only Docker.
/// </summary>
[Collection(WolverineInfrastructureSuite.Name)]
public sealed class WolverineRabbitMqIntegrationTests(WolverineInfrastructureFixture infrastructure)
    : IAsyncLifetime, IDisposable
{
    private static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(3);

    private readonly MessagingTestKeys _keys = new([.. WolverineTestTopology.Principals]);
    private readonly List<WolverineTestHost> _hosts = [];
    private WolverineBrokerTarget _broker = null!;
    private BrokerAdmin _admin = null!;

    public async Task InitializeAsync()
    {
        _broker = await infrastructure.CreateVirtualHostAsync();
        _admin = infrastructure.Admin(_broker);
    }

    public async Task DisposeAsync()
    {
        foreach (var host in _hosts)
            await host.DisposeAsync();
    }

    public void Dispose() => _keys.Dispose();

    [Fact]
    public async Task Command_travels_outbox_to_inbox_once_with_its_tenant_restored()
    {
        var (producer, consumer) = await StartProducerAndConsumerAsync();
        var tenantId = Guid.NewGuid();
        var command = WolverineProviderUnitTests.NewCommand(tenantId);
        var key = command.RequestId.ToString("D");

        await producer.InUnitOfWorkAsync(
            services => services.GetRequiredService<ITenantWrightMessageSender>().SendCommandAsync(command), tenantId);

        Assert.True(await consumer.Probe.WaitForAttemptsAsync(key, 1, DeliveryTimeout), "The command was not handled.");
        Assert.Equal(tenantId, consumer.Probe.TenantFor(key));
        Assert.Equal(1, await consumer.EffectCountAsync(key));

        var row = await WaitForOutboxAsync(producer, message => message.Kind == TenantWrightMessageRouteKind.Command,
            message => message.DispatchedAtUtc is not null);
        var inbox = Assert.Single(await consumer.InboxAsync(row.Id));
        Assert.Equal(WolverineTestTopology.CommandEndpoint, inbox.Endpoint);

        await Task.Delay(Settle);
        Assert.Equal(1, consumer.Probe.AttemptsFor(key));
        Assert.Equal(0u, await ReadyCountAsync(TenantWrightWolverineTopology.DeadLetterQueueName(WolverineTestTopology.CommandEndpoint)));
    }

    [Fact]
    public async Task Event_fans_out_to_every_consuming_endpoint_once()
    {
        var (producer, consumer) = await StartProducerAndConsumerAsync();
        var tenantId = Guid.NewGuid();
        var key = Guid.NewGuid().ToString("D");

        await producer.InUnitOfWorkAsync(
            services => services.GetRequiredService<ITenantWrightMessageSender>().PublishEventAsync(Grants(tenantId, key)),
            tenantId);

        Assert.True(await consumer.Probe.WaitForAttemptsAsync(key, 2, DeliveryTimeout), "The event did not reach both endpoints.");
        var row = await WaitForOutboxAsync(producer, message => message.Kind == TenantWrightMessageRouteKind.Event,
            message => message.DispatchedAtUtc is not null);
        var inbox = await WaitForInboxAsync(consumer, row.Id, 2);
        Assert.Equal(
            [WolverineTestTopology.FirstEventEndpoint, WolverineTestTopology.SecondEventEndpoint],
            inbox.Select(record => record.Endpoint).Order(StringComparer.Ordinal));
        Assert.Equal(tenantId, consumer.Probe.TenantFor(key));

        await Task.Delay(Settle);
        Assert.Equal(2, consumer.Probe.AttemptsFor(key));
        Assert.Equal(2, await consumer.EffectCountAsync(key));
    }

    [Fact]
    public async Task Duplicate_delivery_of_one_message_id_is_handled_once()
    {
        var (producer, consumer) = await StartProducerAndConsumerAsync();
        var tenantId = Guid.NewGuid();
        var command = WolverineProviderUnitTests.NewCommand(tenantId);
        var key = command.RequestId.ToString("D");
        var stamped = producer.Stamp(command, TenantWrightMessageRouteKind.Command, tenantId);
        var transport = producer.Services.GetRequiredService<ITenantWrightMessagingTransport>();
        var dispatch = new TenantWrightTransportMessage(
            stamped.MessageId, stamped.ContractIdentity, stamped.Kind, stamped.Destination, stamped.Payload, stamped.Headers);

        await transport.DispatchAsync(dispatch, CancellationToken.None);
        await transport.DispatchAsync(dispatch, CancellationToken.None);

        Assert.True(await consumer.Probe.WaitForAttemptsAsync(key, 1, DeliveryTimeout), "The command was not handled.");
        Assert.True(await WaitForLogAsync(consumer, "Acknowledged a duplicate delivery"), "The duplicate was never acknowledged.");
        await Task.Delay(Settle);

        Assert.Equal(1, consumer.Probe.AttemptsFor(key));
        Assert.Equal(1, await consumer.EffectCountAsync(key));
        Assert.Single(await consumer.InboxAsync(stamped.MessageId));
        Assert.Equal(0u, await ReadyCountAsync(TenantWrightWolverineTopology.DeadLetterQueueName(WolverineTestTopology.CommandEndpoint)));
    }

    [Fact]
    public async Task Tampered_payload_is_dead_lettered_without_running_the_handler()
    {
        var (producer, consumer) = await StartProducerAndConsumerAsync();
        var tenantId = Guid.NewGuid();
        var command = WolverineProviderUnitTests.NewCommand(tenantId);
        var stamped = producer.Stamp(command, TenantWrightMessageRouteKind.Command, tenantId);
        var tampered = WolverineProviderUnitTests.NewCommand(tenantId);
        tampered.RequestId = command.RequestId;
        tampered.TemplateName = "tampered-template";
        var catalog = producer.Services.GetRequiredService<TenantWrightMessageCatalog>();

        // Published by the genuine producer under its own broker identity; only the body differs from what it signed.
        await using (var genuine = Client(WolverineTestTopology.Producer))
        {
            await genuine.PublishAsync(
                WolverineTestTopology.CommandEndpoint,
                Properties(stamped, WolverineTestTopology.Producer),
                catalog.SerializeCanonical(tampered, typeof(NotificationSendRequestedTenantEto)));
        }

        var deadLetter = await TakeDeadLetterAsync(WolverineTestTopology.CommandEndpoint);
        Assert.Equal(stamped.MessageId.ToString("D"), deadLetter.BasicProperties.MessageId);
        Assert.Equal(WolverineTestTopology.Producer, deadLetter.BasicProperties.UserId);
        Assert.True(deadLetter.BasicProperties.Headers?.ContainsKey("x-death"));
        Assert.Equal(0, consumer.Probe.AttemptsFor(command.RequestId.ToString("D")));
        Assert.Contains(consumer.Logs.Entries, entry => entry.Message.Contains(MessagingSecurityException.InvalidSignature, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Signed_event_from_a_producer_the_catalog_does_not_approve_is_dead_lettered_on_every_endpoint()
    {
        var (_, consumer) = await StartProducerAndConsumerAsync();
        var tenantId = Guid.NewGuid();
        var key = Guid.NewGuid().ToString("D");

        // The outsider holds a key the consumer trusts and signs under its own catalog, which approves it as an origin.
        await using var outsiderCore = OutsiderCore();
        TenantWrightOutboundMessage stamped;
        using (outsiderCore.GetRequiredService<Shared.MultiTenancy.ICurrentTenant>().Change(tenantId))
        using (var scope = outsiderCore.CreateScope())
        {
            stamped = scope.ServiceProvider.GetRequiredService<TenantWrightOutboundMessageStamper>()
                .Stamp(Grants(tenantId, key), TenantWrightMessageRouteKind.Event);
        }

        await using (var outsider = Client(WolverineTestTopology.Outsider))
        {
            await outsider.PublishAsync(WolverineTestTopology.GrantsExchange, Properties(stamped, WolverineTestTopology.Outsider), stamped.Payload);
        }

        await TakeDeadLetterAsync(WolverineTestTopology.FirstEventEndpoint);
        await TakeDeadLetterAsync(WolverineTestTopology.SecondEventEndpoint);
        Assert.Equal(0, consumer.Probe.AttemptsFor(key));
        Assert.Empty(await consumer.InboxAsync(stamped.MessageId));
        Assert.Contains(consumer.Logs.Entries, entry => entry.Message.Contains(MessagingSecurityException.UntrustedProducer, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_producer_cannot_spoof_another_principals_broker_identity()
    {
        var (producer, consumer) = await StartProducerAndConsumerAsync();
        var tenantId = Guid.NewGuid();
        var command = WolverineProviderUnitTests.NewCommand(tenantId);
        var key = command.RequestId.ToString("D");
        var stamped = producer.Stamp(command, TenantWrightMessageRouteKind.Command, tenantId);

        // Authenticated as the outsider while claiming the producer's user-id: the broker refuses the publish.
        await using (var outsider = Client(WolverineTestTopology.Outsider))
        {
            var refused = await Assert.ThrowsAnyAsync<Exception>(() => outsider.PublishAsync(
                WolverineTestTopology.CommandEndpoint, Properties(stamped, WolverineTestTopology.Producer), stamped.Payload));
            Assert.True(
                refused is AlreadyClosedException or OperationInterruptedException or PublishException,
                $"Unexpected refusal {refused.GetType().Name}.");
        }

        // Relaying the producer's genuine signed message with no user-id at all reaches the queue but is rejected.
        await using (var relay = Client(WolverineTestTopology.Outsider))
        {
            var anonymous = Properties(stamped, WolverineTestTopology.Producer);
            anonymous.UserId = null;
            await relay.PublishAsync(WolverineTestTopology.CommandEndpoint, anonymous, stamped.Payload);
        }

        var deadLetter = await TakeDeadLetterAsync(WolverineTestTopology.CommandEndpoint);
        Assert.Null(deadLetter.BasicProperties.UserId);
        await Task.Delay(Settle);
        Assert.Equal(0, consumer.Probe.AttemptsFor(key));
        Assert.Equal(0u, await ReadyCountAsync(TenantWrightWolverineTopology.DeadLetterQueueName(WolverineTestTopology.CommandEndpoint)));
    }

    [Fact]
    public async Task Unroutable_command_is_never_marked_dispatched()
    {
        var (producer, _) = await StartProducerAndConsumerAsync();
        var command = new NotificationSendRequestedHostEto
        {
            RequestId = Guid.NewGuid(),
            TemplateName = "wolverine-test",
            SourceService = WolverineTestTopology.Producer
        };

        await producer.InUnitOfWorkAsync(
            services => services.GetRequiredService<ITenantWrightMessageSender>().SendCommandAsync(command), tenantId: null);

        // No exchange for the destination exists: the broker closes the publish channel.
        var absent = await WaitForOutboxAsync(producer, IsHostCommand, message => message.Attempts >= 1);
        Assert.Null(absent.DispatchedAtUtc);

        // The exchange exists but nothing is bound: the mandatory publish is returned and fails the dispatch.
        await using (var admin = _admin.Connect())
        {
            await admin.DeclareFanoutExchangeAsync(WolverineTestTopology.UnboundCommandEndpoint);
        }

        var returned = await WaitForOutboxAsync(producer, IsHostCommand,
            message => message.LastErrorType == nameof(PublishReturnException) || message.ParkedAtUtc is not null);
        Assert.Null(returned.DispatchedAtUtc);
        Assert.Equal(nameof(PublishReturnException), returned.LastErrorType);

        static bool IsHostCommand(MessagingOutboxMessage message) =>
            message.ContractIdentity == TenantWrightMessageContracts.GetIdentity(typeof(NotificationSendRequestedHostEto));
    }

    [Fact]
    public async Task Transient_handler_failure_is_retried_in_process_and_then_succeeds_once()
    {
        var (producer, consumer) = await StartProducerAndConsumerAsync();
        var tenantId = Guid.NewGuid();
        var command = WolverineProviderUnitTests.NewCommand(tenantId);
        var key = command.RequestId.ToString("D");
        consumer.Probe.FailTransiently(key, times: 2);

        await producer.InUnitOfWorkAsync(
            services => services.GetRequiredService<ITenantWrightMessageSender>().SendCommandAsync(command), tenantId);

        Assert.True(await consumer.Probe.WaitForAttemptsAsync(key, 3, DeliveryTimeout), "The handler was not retried.");
        await WaitForEffectAsync(consumer, key);
        await Task.Delay(Settle);

        Assert.Equal(3, consumer.Probe.AttemptsFor(key));
        Assert.Equal(1, await consumer.EffectCountAsync(key));
        Assert.Equal(0u, await ReadyCountAsync(TenantWrightWolverineTopology.DeadLetterQueueName(WolverineTestTopology.CommandEndpoint)));
    }

    [Fact]
    public async Task Failure_marked_transient_outlasts_the_immediate_retries_on_the_longer_cooldowns_and_succeeds_once()
    {
        var (producer, consumer) = await StartProducerAndConsumerAsync();
        var tenantId = Guid.NewGuid();
        var command = WolverineProviderUnitTests.NewCommand(tenantId);
        var key = command.RequestId.ToString("D");
        // Three failures would dead-letter an unmarked message after the two immediate retries.
        consumer.Probe.FailTransiently(key, times: 3, markedTransient: true);

        await producer.InUnitOfWorkAsync(
            services => services.GetRequiredService<ITenantWrightMessageSender>().SendCommandAsync(command), tenantId);

        var cooldowns = TenantWrightWolverineRegistration.TransientRetryCooldowns;
        var expectedWait = cooldowns[0] + cooldowns[1] + cooldowns[2];
        Assert.True(
            await consumer.Probe.WaitForAttemptsAsync(key, 4, DeliveryTimeout + expectedWait),
            "The transient failure was not retried on the longer schedule.");
        await WaitForEffectAsync(consumer, key);
        await Task.Delay(Settle);

        Assert.Equal(4, consumer.Probe.AttemptsFor(key));
        Assert.Equal(1, await consumer.EffectCountAsync(key));
        Assert.Equal(0u, await ReadyCountAsync(TenantWrightWolverineTopology.DeadLetterQueueName(WolverineTestTopology.CommandEndpoint)));

        // Each retry waited at least its cooldown (allowing for clock granularity), never the 250/1000 ms schedule.
        var times = consumer.Probe.AttemptTimesFor(key);
        for (var retry = 0; retry < 3; retry++)
        {
            Assert.True(
                times[retry + 1] - times[retry] >= cooldowns[retry] - TimeSpan.FromMilliseconds(100),
                $"Retry {retry + 1} came after {(times[retry + 1] - times[retry]).TotalMilliseconds} ms.");
        }
    }

    [Fact]
    public async Task Failed_command_is_signed_and_authorized_replay_commits_one_tenant_effect()
    {
        var (producer, consumer) = await StartProducerAndConsumerAsync();
        var tenantId = Guid.NewGuid();
        var command = WolverineProviderUnitTests.NewCommand(tenantId);
        var key = command.RequestId.ToString("D");
        consumer.Probe.FailTransiently(key, times: 3);
        await producer.InUnitOfWorkAsync(
            services => services.GetRequiredService<ITenantWrightMessageSender>().SendCommandAsync(command), tenantId);
        var terminal = await TakeDeadLetterAsync(WolverineTestTopology.CommandEndpoint);
        Assert.Equal(WolverineTestTopology.Consumer, terminal.BasicProperties.UserId);
        Assert.False(terminal.BasicProperties.Headers!.ContainsKey("x-death"));
        Assert.Equal(0, await consumer.EffectCountAsync(key));

        await using var replay = ReplayOperator(consumer, terminal, WolverineTestTopology.CommandEndpoint);
        var material = replay.GetRequiredService<WolverineTerminalMessageReader>()
            .Read(WolverineTestTopology.CommandEndpoint, terminal.BasicProperties, terminal.Body);
        var request = ReplayRequest(material);
        await ReplayAsync(replay, request);
        await WaitForEffectAsync(consumer, key);
        Assert.Equal(4, consumer.Probe.AttemptsFor(key));
        Assert.Equal(tenantId, consumer.Probe.TenantFor(key));
        Assert.Single(await consumer.InboxAsync(material.MessageId));
        Assert.Single(replay.GetRequiredService<ReplayAudit>().Requests);

        // The exact same ID cannot issue twice, and a fresh broker publication cannot reuse its consumed authority.
        await Assert.ThrowsAsync<MessagingSecurityException>(() => ReplayAsync(replay, request));
        await using var scope = replay.CreateAsyncScope();
        var replayRow = await scope.ServiceProvider.GetRequiredService<WolverineTestDbContext>()
            .Set<TenantWrightReplayState>().SingleAsync(state => state.ReplayId == request.ReplayId);
        Assert.NotNull(replayRow.ConsumedAt);
        var replayAttestation = replay.GetRequiredService<TenantWrightAttestationService>().CreateReplay(
            material.TerminalRecordSha256, material.OriginalChainSha256, material.MessageId, material.PayloadSha256,
            request.Destination, request.ReplayId, request.AuditId, replayRow.ExpiresAt.AddMinutes(-15), replayRow.ExpiresAt);
        var (properties, body) = WolverineProtectedReplayTransport.CreatePublication(
            new ProtectedReplayDispatch(material, replayAttestation), WolverineTestTopology.ProducerCatalog(),
            replay.GetRequiredService<TenantWrightAttestationService>(),
            replay.GetRequiredService<Microsoft.Extensions.Options.IOptions<MessagingOptions>>().Value, DateTimeOffset.UtcNow);
        await using var client = Client(WolverineTestTopology.Replay);
        await client.PublishAsync(request.Destination, properties, body);
        await TakeDeadLetterAsync(request.Destination);
        Assert.Equal(4, consumer.Probe.AttemptsFor(key));
        Assert.Equal(1, await consumer.EffectCountAsync(key));
    }

    [Fact]
    public async Task Event_replay_targets_only_the_failed_endpoint_instead_of_fanning_out()
    {
        var (producer, consumer) = await StartProducerAndConsumerAsync();
        var tenantId = Guid.NewGuid();
        var key = Guid.NewGuid().ToString("D");
        var stamped = producer.Stamp(Grants(tenantId, key), TenantWrightMessageRouteKind.Event, tenantId);
        consumer.Probe.FailTransiently(key, times: 3);
        await using var client = Client(WolverineTestTopology.Producer);
        await client.PublishAsync(WolverineTestTopology.FirstEventEndpoint, Properties(stamped, WolverineTestTopology.Producer), stamped.Payload);
        var terminal = await TakeDeadLetterAsync(WolverineTestTopology.FirstEventEndpoint);

        await using var replay = ReplayOperator(consumer, terminal, WolverineTestTopology.FirstEventEndpoint);
        var material = replay.GetRequiredService<WolverineTerminalMessageReader>()
            .Read(WolverineTestTopology.FirstEventEndpoint, terminal.BasicProperties, terminal.Body);
        await ReplayAsync(replay, ReplayRequest(material));
        await WaitForEffectAsync(consumer, key);
        await Task.Delay(Settle);

        Assert.Equal(4, consumer.Probe.AttemptsFor(key));
        Assert.Equal(1, await consumer.EffectCountAsync(key));
        Assert.Equal(WolverineTestTopology.FirstEventEndpoint, Assert.Single(await consumer.InboxAsync(material.MessageId)).Endpoint);
    }

    [Fact]
    public async Task Failed_replay_is_terminal_after_one_attempt_and_can_receive_fresh_authorization()
    {
        var (producer, consumer) = await StartProducerAndConsumerAsync();
        var tenantId = Guid.NewGuid();
        var command = WolverineProviderUnitTests.NewCommand(tenantId);
        var key = command.RequestId.ToString("D");
        consumer.Probe.FailTransiently(key, times: 10, markedTransient: false);
        await producer.InUnitOfWorkAsync(
            services => services.GetRequiredService<ITenantWrightMessageSender>().SendCommandAsync(command), tenantId);
        var firstTerminal = await TakeDeadLetterAsync(WolverineTestTopology.CommandEndpoint);
        await using (var replay = ReplayOperator(consumer, firstTerminal, WolverineTestTopology.CommandEndpoint))
        {
            var material = replay.GetRequiredService<WolverineTerminalMessageReader>()
                .Read(WolverineTestTopology.CommandEndpoint, firstTerminal.BasicProperties, firstTerminal.Body);
            await ReplayAsync(replay, ReplayRequest(material));
        }
        var secondTerminal = await TakeDeadLetterAsync(WolverineTestTopology.CommandEndpoint);
        Assert.Equal(4, consumer.Probe.AttemptsFor(key));
        Assert.Equal(0, await consumer.EffectCountAsync(key));
        Assert.DoesNotContain(secondTerminal.BasicProperties.Headers!.Keys, TenantWrightTrustHeaders.Replay.Contains);
        consumer.Probe.FailTransiently(key, times: 0);

        await using (var replay = ReplayOperator(consumer, secondTerminal, WolverineTestTopology.CommandEndpoint))
        {
            var material = replay.GetRequiredService<WolverineTerminalMessageReader>()
                .Read(WolverineTestTopology.CommandEndpoint, secondTerminal.BasicProperties, secondTerminal.Body);
            await ReplayAsync(replay, ReplayRequest(material));
        }
        await WaitForEffectAsync(consumer, key);
        Assert.Equal(5, consumer.Probe.AttemptsFor(key));
        Assert.Equal(1, await consumer.EffectCountAsync(key));
    }

    [Fact]
    public async Task Denied_operator_approval_cannot_issue_or_dispatch_a_replay()
    {
        var (producer, consumer) = await StartProducerAndConsumerAsync();
        var tenantId = Guid.NewGuid();
        var command = WolverineProviderUnitTests.NewCommand(tenantId);
        var key = command.RequestId.ToString("D");
        consumer.Probe.FailTransiently(key, times: 3);
        await producer.InUnitOfWorkAsync(
            services => services.GetRequiredService<ITenantWrightMessageSender>().SendCommandAsync(command), tenantId);
        var terminal = await TakeDeadLetterAsync(WolverineTestTopology.CommandEndpoint);
        await using var replay = ReplayOperator(consumer, terminal, WolverineTestTopology.CommandEndpoint);
        var material = replay.GetRequiredService<WolverineTerminalMessageReader>()
            .Read(WolverineTestTopology.CommandEndpoint, terminal.BasicProperties, terminal.Body);
        var request = ReplayRequest(material) with { AuthorizationId = "unapproved" };
        await Assert.ThrowsAsync<MessagingSecurityException>(() => ReplayAsync(replay, request));
        Assert.Empty(replay.GetRequiredService<ReplayAudit>().Requests);
        await using var context = consumer.ReadContext();
        Assert.False(await context.Set<TenantWrightReplayState>().AnyAsync(state => state.ReplayId == request.ReplayId));
        Assert.Equal(0, await consumer.EffectCountAsync(key));
    }

    [Fact]
    public async Task Terminal_publish_failure_keeps_the_original_recoverable_until_the_exchange_returns()
    {
        var (producer, consumer) = await StartProducerAndConsumerAsync();
        var tenantId = Guid.NewGuid();
        var command = WolverineProviderUnitTests.NewCommand(tenantId);
        var key = command.RequestId.ToString("D");
        var exchange = TenantWrightWolverineTopology.DeadLetterQueueName(WolverineTestTopology.CommandEndpoint);
        consumer.Probe.FailTransiently(key, times: 1000);
        await using var admin = _admin.Connect();
        await admin.DeleteExchangeAsync(exchange);
        await producer.InUnitOfWorkAsync(
            services => services.GetRequiredService<ITenantWrightMessageSender>().SendCommandAsync(command), tenantId);
        var deadline = DateTime.UtcNow + DeliveryTimeout;
        while (!consumer.Logs.Entries.Any(entry => entry.Message.StartsWith("Terminal custody was not settled", StringComparison.Ordinal)) &&
               DateTime.UtcNow < deadline)
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        Assert.Contains(consumer.Logs.Entries, entry => entry.Message.StartsWith("Terminal custody was not settled", StringComparison.Ordinal));
        Assert.Equal(HealthStatus.Unhealthy, (await consumer.ReadinessAsync()).Status);
        Assert.Equal(0, await consumer.EffectCountAsync(key));

        await admin.DeclareFanoutExchangeAsync(exchange);
        await admin.BindAsync(exchange, exchange);
        var terminal = await TakeDeadLetterAsync(WolverineTestTopology.CommandEndpoint);
        var reader = consumer.Services.GetRequiredService<WolverineTerminalMessageReader>();
        Assert.True(reader.Read(WolverineTestTopology.CommandEndpoint, terminal.BasicProperties, terminal.Body).Summary.ProvenanceVerified);
        Assert.Equal(0, await consumer.EffectCountAsync(key));
        var recovered = DateTime.UtcNow + DeliveryTimeout;
        while (consumer.Services.GetRequiredService<WolverineTerminalPublisher>().HasBlockedPublications && DateTime.UtcNow < recovered)
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        Assert.Equal(HealthStatus.Healthy, (await consumer.ReadinessAsync()).Status);
    }

    [Fact]
    public async Task Shutdown_during_terminal_publication_leaves_the_original_for_a_replacement_consumer()
    {
        var (producer, consumer) = await StartProducerAndConsumerAsync();
        string connectionString;
        using (var context = consumer.ReadContext())
            connectionString = context.Database.GetConnectionString()!;
        var tenantId = Guid.NewGuid();
        var command = WolverineProviderUnitTests.NewCommand(tenantId);
        var key = command.RequestId.ToString("D");
        consumer.Probe.FailTransiently(key, times: 1000);
        await using var admin = _admin.Connect();
        await admin.DeleteExchangeAsync(TenantWrightWolverineTopology.DeadLetterQueueName(WolverineTestTopology.CommandEndpoint));
        await producer.InUnitOfWorkAsync(
            services => services.GetRequiredService<ITenantWrightMessageSender>().SendCommandAsync(command), tenantId);
        var deadline = DateTime.UtcNow + DeliveryTimeout;
        while (!consumer.Services.GetRequiredService<WolverineTerminalPublisher>().HasBlockedPublications && DateTime.UtcNow < deadline)
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        Assert.True(consumer.Services.GetRequiredService<WolverineTerminalPublisher>().HasBlockedPublications);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await consumer.Host.StopAsync(timeout.Token);
        var replacement = await StartHostAsync(WolverineTestHost.StartAsync(connectionString,
            WolverineTestConfiguration.Messaging(_broker, _keys, WolverineTestTopology.Consumer, WolverineTestTopology.Principals),
            WolverineTestTopology.Consumer, WolverineTestTopology.ConsumerCatalog()));
        await WaitForEffectAsync(replacement, key);
        Assert.Equal(1, await replacement.EffectCountAsync(key));
        Assert.Equal(tenantId, replacement.Probe.TenantFor(key));
    }

    [Fact]
    public async Task Endpoint_owner_declares_quorum_queues_dead_letter_queues_and_bindings()
    {
        await StartProducerAndConsumerAsync();

        foreach (var endpoint in new[]
                 {
                     WolverineTestTopology.CommandEndpoint, WolverineTestTopology.FirstEventEndpoint,
                     WolverineTestTopology.SecondEventEndpoint
                 })
        {
            var queue = await _admin.QueueAsync(endpoint);
            Assert.NotNull(queue);
            Assert.Equal("quorum", queue.Type);
            Assert.True(queue.Durable);
            Assert.Contains(TenantWrightWolverineTopology.DeadLetterQueueName(endpoint), queue.Arguments, StringComparison.Ordinal);
            Assert.Contains("at-least-once", queue.Arguments, StringComparison.Ordinal);

            var deadLetter = await _admin.QueueAsync(TenantWrightWolverineTopology.DeadLetterQueueName(endpoint));
            Assert.NotNull(deadLetter);
            Assert.Equal("quorum", deadLetter.Type);
        }

        Assert.Contains(WolverineTestTopology.CommandEndpoint, await _admin.BindingSourcesAsync(WolverineTestTopology.CommandEndpoint));
        Assert.Contains(WolverineTestTopology.GrantsExchange, await _admin.BindingSourcesAsync(WolverineTestTopology.FirstEventEndpoint));
        Assert.Contains(WolverineTestTopology.GrantsExchange, await _admin.BindingSourcesAsync(WolverineTestTopology.SecondEventEndpoint));
        Assert.Contains(WolverineTestTopology.FirstEventEndpoint, await _admin.BindingSourcesAsync(WolverineTestTopology.FirstEventEndpoint));
        Assert.Contains(WolverineTestTopology.SecondEventEndpoint, await _admin.BindingSourcesAsync(WolverineTestTopology.SecondEventEndpoint));

        // The producer never declares a consumer's queue, and nothing declares the unbound destination.
        Assert.Null(await _admin.QueueAsync(WolverineTestTopology.UnboundCommandEndpoint));
    }

    [Fact]
    public async Task Real_topology_host_starts_quietly_in_development_with_apphost_style_settings()
    {
        const string principal = "tenantwright-tenant";
        using var keys = new MessagingTestKeys(principal);
        var settings = WolverineTestConfiguration.Messaging(_broker, keys, principal, [principal]);
        var host = await StartHostAsync(
            WolverineTestHost.StartWithRealTopologyAsync(await infrastructure.CreateDatabaseAsync(), settings, principal));

        Assert.Empty(host.StartupWarnings);
        Assert.Equal(HealthStatus.Healthy, (await host.ReadinessAsync()).Status);
        foreach (var endpoint in TenantWrightMessagingTopology.ReceiveEndpointsOwnedBy(principal))
            Assert.Equal("quorum", (await _admin.QueueAsync(endpoint.Name))?.Type);
    }

    private async Task<(WolverineTestHost Producer, WolverineTestHost Consumer)> StartProducerAndConsumerAsync()
    {
        var consumer = await StartHostAsync(WolverineTestHost.StartAsync(
            await infrastructure.CreateDatabaseAsync(),
            WolverineTestConfiguration.Messaging(_broker, _keys, WolverineTestTopology.Consumer, WolverineTestTopology.Principals),
            WolverineTestTopology.Consumer,
            WolverineTestTopology.ConsumerCatalog()));
        var producer = await StartHostAsync(WolverineTestHost.StartAsync(
            await infrastructure.CreateDatabaseAsync(),
            WolverineTestConfiguration.Messaging(_broker, _keys, WolverineTestTopology.Producer, WolverineTestTopology.Principals),
            WolverineTestTopology.Producer,
            WolverineTestTopology.ProducerCatalog()));

        foreach (var host in new[] { consumer, producer })
        {
            Assert.True(host.StartupWarnings.Count == 0,
                $"{host.Principal} logged during startup: {string.Join(" | ", host.StartupWarnings)}");
            var readiness = await host.ReadinessAsync();
            Assert.True(readiness.Status == HealthStatus.Healthy,
                $"{host.Principal} is not ready: {string.Join(" | ", readiness.Entries.Select(entry => $"{entry.Key}={entry.Value.Status} {entry.Value.Description}"))}");
        }

        return (producer, consumer);
    }

    private async Task<WolverineTestHost> StartHostAsync(Task<WolverineTestHost> starting)
    {
        var host = await starting;
        _hosts.Add(host);
        return host;
    }

    private RawBrokerClient Client(string principal) =>
        new(_broker.HostName, _broker.Port, _broker.VirtualHost, principal, _broker.Password);

    private ServiceProvider ReplayOperator(WolverineTestHost consumer, BasicGetResult terminal, string endpoint)
    {
        using var context = consumer.ReadContext();
        var connectionString = context.Database.GetConnectionString()!;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(new TestHostEnvironment("Development"));
        services.AddSingleton<Shared.MultiTenancy.ICurrentTenant, TestAmbientTenant>();
        var configuration = WolverineTestConfiguration.Build(WolverineTestConfiguration.Messaging(
            _broker, _keys, WolverineTestTopology.Replay, WolverineTestTopology.Principals));
        var catalog = WolverineTestTopology.ProducerCatalog();
        services.AddTenantWrightMessagingCore(configuration, catalog);
        new WolverineMessagingProvider().Register(services, configuration, catalog);
        services.AddScoped(_ => new WolverineTestDbContext(
            new DbContextOptionsBuilder<WolverineTestDbContext>().UseNpgsql(connectionString).Options));
        services.AddTenantWrightProtectedReplayIdempotencyStore<WolverineTestDbContext>();
        services.AddSingleton<IProtectedTerminalStore>(provider => new ReplaySnapshotStore(
            provider.GetRequiredService<WolverineTerminalMessageReader>().Read(endpoint, terminal.BasicProperties, terminal.Body)));
        services.AddSingleton<IProtectedReplayAuthorization, ReplayAuthorization>();
        services.AddSingleton<ReplayAudit>();
        services.AddSingleton<IProtectedReplayAudit>(provider => provider.GetRequiredService<ReplayAudit>());
        return services.BuildServiceProvider();
    }

    private static ProtectedReplayRequest ReplayRequest(ProtectedReplayMaterial material) => new(
        material.Summary.TerminalId, material.Summary.Endpoint, "Test operator recovery", "approved-operator",
        Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"));

    private static async Task ReplayAsync(ServiceProvider provider, ProtectedReplayRequest request)
    {
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ProtectedReplayService>().ReplayAsync(request);
    }

    private sealed class ReplaySnapshotStore(ProtectedReplayMaterial material) : IProtectedTerminalStore
    {
        public Task<ProtectedReplayMaterial?> FindAsync(string terminalId, CancellationToken cancellationToken) =>
            Task.FromResult(terminalId == material.Summary.TerminalId ? material : null);
    }

    private sealed class ReplayAuthorization : IProtectedReplayAuthorization
    {
        public Task AuthorizeAsync(ProtectedReplayRequest request, ProtectedTerminalSummary terminal, CancellationToken cancellationToken)
        {
            if (request.AuthorizationId != "approved-operator")
                throw new MessagingSecurityException(MessagingSecurityException.ReplayRejected);
            return Task.CompletedTask;
        }
    }

    private sealed class ReplayAudit : IProtectedReplayAudit
    {
        public List<ProtectedReplayRequest> Requests { get; } = [];
        public Task RecordAsync(ProtectedReplayRequest request, ProtectedTerminalSummary terminal, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.CompletedTask;
        }
    }

    private ServiceProvider OutsiderCore()
    {
        var configuration = WolverineTestConfiguration.Build(WolverineTestConfiguration.Messaging(
            _broker, _keys, WolverineTestTopology.Outsider, WolverineTestTopology.Principals));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(new TestHostEnvironment("Development"));
        services.AddSingleton<Shared.MultiTenancy.ICurrentTenant, TestAmbientTenant>();
        services.AddTenantWrightMessagingCore(configuration, WolverineTestTopology.Create([], outsiderMayPublishGrants: true));
        return services.BuildServiceProvider();
    }

    private static PermissionGrantsChangedTenantEto Grants(Guid tenantId, string key) => new()
    {
        TenantId = tenantId,
        ProviderName = "wolverine-test",
        ProviderKey = key,
        Stamp = 1,
        OccurredAtUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
    };

    /// <summary>The AMQP properties the production transport would set for <paramref name="stamped"/>.</summary>
    private static BasicProperties Properties(TenantWrightOutboundMessage stamped, string userId) =>
        WolverineMessagingTransport.CreateProperties(
            new TenantWrightTransportMessage(stamped.MessageId, stamped.ContractIdentity, stamped.Kind, stamped.Destination,
                stamped.Payload, stamped.Headers),
            userId,
            DateTimeOffset.UtcNow);

    private async Task<uint?> ReadyCountAsync(string queue)
    {
        await using var admin = _admin.Connect();
        return await admin.ReadyCountAsync(queue);
    }

    private async Task<BasicGetResult> TakeDeadLetterAsync(string endpoint)
    {
        var queue = TenantWrightWolverineTopology.DeadLetterQueueName(endpoint);
        await using var admin = _admin.Connect();
        var deadline = DateTime.UtcNow + DeliveryTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await admin.TakeAsync(queue) is { } message)
                return message;
            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        throw new TimeoutException($"Nothing was dead-lettered to {queue}.");
    }

    private static async Task<MessagingOutboxMessage> WaitForOutboxAsync(
        WolverineTestHost host,
        Func<MessagingOutboxMessage, bool> match,
        Func<MessagingOutboxMessage, bool> condition)
    {
        var deadline = DateTime.UtcNow + DeliveryTimeout;
        MessagingOutboxMessage? row = null;
        while (DateTime.UtcNow < deadline)
        {
            row = await host.OutboxRowAsync(match);
            if (row is not null && condition(row))
                return row;
            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }

        throw new TimeoutException(
            $"The outbox row never reached the expected state (attempts={row?.Attempts}, dispatched={row?.DispatchedAtUtc}, error={row?.LastErrorType}).");
    }

    private static async Task<IReadOnlyList<MessagingInboxRecord>> WaitForInboxAsync(WolverineTestHost host, Guid messageId, int expected)
    {
        var deadline = DateTime.UtcNow + DeliveryTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var records = await host.InboxAsync(messageId);
            if (records.Count >= expected)
                return records;
            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }

        return await host.InboxAsync(messageId);
    }

    private static async Task WaitForEffectAsync(WolverineTestHost host, string key)
    {
        var deadline = DateTime.UtcNow + DeliveryTimeout;
        while (DateTime.UtcNow < deadline && await host.EffectCountAsync(key) == 0)
            await Task.Delay(TimeSpan.FromMilliseconds(200));
    }

    private static async Task<bool> WaitForLogAsync(WolverineTestHost host, string fragment)
    {
        var deadline = DateTime.UtcNow + DeliveryTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (host.Logs.Entries.Any(entry => entry.Level >= LogLevel.Information &&
                                               entry.Message.Contains(fragment, StringComparison.Ordinal)))
                return true;
            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }

        return false;
    }
}
