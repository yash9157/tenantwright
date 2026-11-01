using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shared.EntityFrameworkCore.Messaging;
using Shared.EntityFrameworkCore.Tests.TestDoubles;
using Shared.EventBus.Messaging;
using Shared.EventContracts;
using Xunit;

namespace Shared.EntityFrameworkCore.Tests;

/// <summary>
/// The provider-neutral outbox and inbox against real PostgreSQL. No broker and no MassTransit license are involved:
/// a <see cref="LoopbackTransport"/> stands in for the transport seam.
/// </summary>
[Collection(NeutralMessagingPostgresSuite.Name)]
[Trait("Category", "PostgresIntegration")]
public sealed class NeutralMessagingPostgresIntegrationTests(NeutralMessagingPostgresFixture postgres) : IDisposable
{
    private readonly MessagingTrustKeys _keys = new(NeutralMessagingTopology.Producer, NeutralMessagingTopology.Consumer);

    public void Dispose() => _keys.Dispose();

    [Fact]
    public async Task Outbox_row_should_commit_with_the_callers_transaction_and_roll_back_with_it()
    {
        await using var producer = await CreateProducerAsync();
        var tenantId = Guid.NewGuid();
        var committedKey = Guid.NewGuid();
        var rolledBackKey = Guid.NewGuid();

        await producer.InUnitOfWorkAsync(async (services, dbContext) =>
        {
            dbContext.ProducerWrites.Add(new ProducerWrite(committedKey));
            await NeutralMessagingHost.PublishAsync(services, tenantId, committedKey);

            // Tracked by the exact context the unit of work saves and commits, inside its transaction.
            Assert.Contains(dbContext.ChangeTracker.Entries(), entry => entry.Entity is MessagingOutboxMessage);
            Assert.NotNull(dbContext.Database.CurrentTransaction);
            await dbContext.SaveChangesAsync();

            // Saved but uncommitted: invisible to any other connection.
            Assert.Empty(await producer.OutboxAsync());
        }, tenantId: tenantId);

        await producer.InUnitOfWorkAsync(async (services, dbContext) =>
        {
            dbContext.ProducerWrites.Add(new ProducerWrite(rolledBackKey));
            await NeutralMessagingHost.PublishAsync(services, tenantId, rolledBackKey);
            await dbContext.SaveChangesAsync();
        }, complete: false, tenantId: tenantId);

        var outbox = Assert.Single(await producer.OutboxAsync());
        Assert.Contains(committedKey.ToString("D"), System.Text.Encoding.UTF8.GetString(outbox.Payload), StringComparison.Ordinal);
        Assert.Equal("Shared.EventContracts.PermissionGrantsChangedTenantEto", outbox.ContractIdentity);
        Assert.Equal(TenantWrightMessageRouteKind.Event, outbox.Kind);
        Assert.Null(outbox.Destination);
        Assert.Equal(tenantId, outbox.TenantId);
        Assert.Equal(0, outbox.Attempts);
        Assert.Null(outbox.DispatchedAtUtc);
        Assert.Contains(NeutralMessagingTopology.Producer, outbox.Headers, StringComparison.Ordinal);
        Assert.Contains(TenantWrightTrustHeaders.OriginSignature, outbox.Headers, StringComparison.Ordinal);

        await using var read = producer.ReadContext();
        Assert.True(await read.ProducerWrites.AnyAsync(write => write.MessageId == committedKey));
        Assert.False(await read.ProducerWrites.AnyAsync(write => write.MessageId == rolledBackKey));
    }

    [Fact]
    public async Task Sender_without_an_ambient_unit_of_work_should_commit_its_own_short_transaction()
    {
        await using var producer = await CreateProducerAsync();
        var tenantId = Guid.NewGuid();

        using (producer.CurrentTenant.Change(tenantId))
        {
            await using var scope = producer.Services.CreateAsyncScope();
            await NeutralMessagingHost.PublishAsync(scope.ServiceProvider, tenantId, Guid.NewGuid());
        }

        Assert.Single(await producer.OutboxAsync());
    }

    [Fact]
    public async Task Independent_inner_unit_of_work_should_commit_its_message_while_the_outer_rolls_back()
    {
        await using var producer = await CreateProducerAsync();
        var tenantId = Guid.NewGuid();
        var innerKey = Guid.NewGuid();

        await producer.InUnitOfWorkAsync(async (outerServices, _) =>
        {
            await NeutralMessagingHost.PublishAsync(outerServices, tenantId, Guid.NewGuid());
            await producer.InUnitOfWorkAsync(
                (innerServices, _) => NeutralMessagingHost.PublishAsync(innerServices, tenantId, innerKey),
                tenantId: tenantId);
        }, complete: false, tenantId: tenantId);

        var outbox = Assert.Single(await producer.OutboxAsync());
        Assert.Contains(innerKey.ToString("D"), System.Text.Encoding.UTF8.GetString(outbox.Payload), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tenant_mismatch_should_write_no_outbox_row()
    {
        await using var producer = await CreateProducerAsync();

        await Assert.ThrowsAsync<MessagingSecurityException>(() => producer.InUnitOfWorkAsync(
            (services, _) => NeutralMessagingHost.PublishAsync(services, Guid.NewGuid(), Guid.NewGuid()),
            tenantId: Guid.NewGuid()));
        await Assert.ThrowsAsync<MessagingSecurityException>(() => producer.InUnitOfWorkAsync(
            (services, _) => NeutralMessagingHost.PublishAsync(services, Guid.NewGuid(), Guid.NewGuid())));

        Assert.Empty(await producer.OutboxAsync());
    }

    [Fact]
    public async Task Concurrent_dispatchers_should_claim_disjoint_rows_and_dispatch_each_message_exactly_once()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var transport = new LoopbackTransport(NeutralMessagingTopology.Producer) { Latency = TimeSpan.FromMilliseconds(3) };
        var settings = new Dictionary<string, string?> { [$"{TenantWrightOutboxOptions.SectionName}:BatchSize"] = "7" };
        await using var first = NeutralMessagingHost.Create(connectionString, _keys, NeutralMessagingTopology.Producer,
            consumes: false, settings, transport: transport);
        await using var second = NeutralMessagingHost.Create(connectionString, _keys, NeutralMessagingTopology.Producer,
            consumes: false, settings, transport: transport);
        var tenantId = Guid.NewGuid();
        const int messageCount = 120;
        for (var batch = 0; batch < 6; batch++)
        {
            await first.InUnitOfWorkAsync(async (services, _) =>
            {
                for (var index = 0; index < messageCount / 6; index++)
                    await NeutralMessagingHost.PublishAsync(services, tenantId, Guid.NewGuid());
            }, tenantId: tenantId);
        }

        // Both dispatchers race over the same table until it is drained.
        await Task.WhenAll(
            Task.Run(() => first.DrainAsync(TimeSpan.FromMinutes(2))),
            Task.Run(() => second.DrainAsync(TimeSpan.FromMinutes(2))),
            Task.Run(() => first.DrainAsync(TimeSpan.FromMinutes(2))),
            Task.Run(() => second.DrainAsync(TimeSpan.FromMinutes(2))));

        var outbox = await first.OutboxAsync();
        Assert.Equal(messageCount, outbox.Count);
        Assert.All(outbox, row =>
        {
            Assert.NotNull(row.DispatchedAtUtc);
            Assert.Null(row.LockedBy);
            Assert.Equal(1, row.Attempts);
        });
        Assert.Equal(messageCount, transport.Dispatches.Count);
        Assert.All(transport.Dispatches.Values, count => Assert.Equal(1, count));
    }

    [Fact]
    public async Task Failing_dispatch_should_back_off_and_park_after_the_attempt_cap()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var transport = new LoopbackTransport(NeutralMessagingTopology.Producer)
        {
            Fault = _ => new TimeoutException("broker unavailable")
        };
        await using var producer = NeutralMessagingHost.Create(connectionString, _keys, NeutralMessagingTopology.Producer,
            consumes: false,
            new Dictionary<string, string?> { [$"{TenantWrightOutboxOptions.SectionName}:MaxAttempts"] = "3" },
            transport: transport);
        var tenantId = Guid.NewGuid();
        await producer.InUnitOfWorkAsync((services, _) => NeutralMessagingHost.PublishAsync(services, tenantId, Guid.NewGuid()),
            tenantId: tenantId);

        Assert.Equal(1, await producer.Dispatcher.DispatchBatchAsync(CancellationToken.None));
        var afterFirst = Assert.Single(await producer.OutboxAsync());
        Assert.Equal(1, afterFirst.Attempts);
        Assert.Null(afterFirst.ParkedAtUtc);
        Assert.Null(afterFirst.LockedBy);
        Assert.Equal(nameof(TimeoutException), afterFirst.LastErrorType);
        Assert.True(afterFirst.AvailableAtUtc > afterFirst.CreatedAtUtc, "A failed attempt must be scheduled into the future.");

        // Not due yet: the backoff keeps the row out of an immediate claim.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        MessagingOutboxMessage current;
        do
        {
            await Task.Delay(TimeSpan.FromMilliseconds(60));
            await producer.Dispatcher.DispatchBatchAsync(CancellationToken.None);
            current = Assert.Single(await producer.OutboxAsync());
        }
        while (current.ParkedAtUtc is null && DateTime.UtcNow < deadline);

        Assert.NotNull(current.ParkedAtUtc);
        Assert.Equal(3, current.Attempts);
        Assert.Null(current.DispatchedAtUtc);
        Assert.Equal(nameof(TimeoutException), current.LastErrorType);
        Assert.Empty(transport.Dispatches);

        // A parked row is never claimed again.
        Assert.Equal(0, await producer.Dispatcher.DispatchBatchAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Transient_dispatch_failure_should_be_retried_until_confirmed()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var failures = 0;
        var transport = new LoopbackTransport(NeutralMessagingTopology.Producer)
        {
            Fault = _ => Interlocked.Increment(ref failures) <= 2 ? new IOException("connection reset") : null
        };
        await using var producer = NeutralMessagingHost.Create(connectionString, _keys, NeutralMessagingTopology.Producer,
            consumes: false, transport: transport);
        var tenantId = Guid.NewGuid();
        await producer.InUnitOfWorkAsync((services, _) => NeutralMessagingHost.PublishAsync(services, tenantId, Guid.NewGuid()),
            tenantId: tenantId);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        MessagingOutboxMessage row;
        do
        {
            await producer.Dispatcher.DispatchBatchAsync(CancellationToken.None);
            await Task.Delay(TimeSpan.FromMilliseconds(60));
            row = Assert.Single(await producer.OutboxAsync());
        }
        while (row.DispatchedAtUtc is null && DateTime.UtcNow < deadline);

        Assert.NotNull(row.DispatchedAtUtc);
        Assert.Equal(3, row.Attempts);
        Assert.Null(row.LastErrorType);
        Assert.Equal(1, Assert.Single(transport.Dispatches).Value);
    }

    [Fact]
    public async Task Origin_that_consumers_would_reject_as_stale_should_be_parked_instead_of_dispatched()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var producer = await CreateProducerAsync(connectionString);
        var tenantId = Guid.NewGuid();
        await producer.InUnitOfWorkAsync((services, _) => NeutralMessagingHost.PublishAsync(services, tenantId, Guid.NewGuid()),
            tenantId: tenantId);
        await ExecuteAsync(connectionString, """
            UPDATE public."MessagingOutboxMessages"
            SET "Headers" = jsonb_set("Headers", '{x-tenantwright-origin-issued-at}', to_jsonb('2020-01-01T00:00:00.0000000Z'::text))
            """);

        await producer.Dispatcher.DispatchBatchAsync(CancellationToken.None);

        var row = Assert.Single(await producer.OutboxAsync());
        Assert.NotNull(row.ParkedAtUtc);
        Assert.Equal("OriginExpired", row.LastErrorType);
        Assert.Empty(producer.Transport.Dispatches);
    }

    [Fact]
    public async Task Retention_should_delete_only_expired_dispatched_rows_and_expired_inbox_records_in_bounded_batches()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var producer = NeutralMessagingHost.Create(connectionString, _keys, NeutralMessagingTopology.Producer,
            consumes: false,
            new Dictionary<string, string?>
            {
                [$"{TenantWrightOutboxOptions.SectionName}:CleanupBatchSize"] = "2",
                [$"{TenantWrightInboxOptions.SectionName}:CleanupBatchSize"] = "2"
            });
        var tenantId = Guid.NewGuid();
        await producer.InUnitOfWorkAsync(async (services, _) =>
        {
            for (var index = 0; index < 8; index++)
                await NeutralMessagingHost.PublishAsync(services, tenantId, Guid.NewGuid());
        }, tenantId: tenantId);
        await producer.DrainAsync(TimeSpan.FromSeconds(30));

        var ids = (await producer.OutboxAsync()).Select(row => row.Id).ToArray();
        await ExecuteAsync(connectionString, $"""
            UPDATE public."MessagingOutboxMessages" SET "DispatchedAtUtc" = now() - interval '8 days'
            WHERE "Id" IN ('{ids[0]}', '{ids[1]}', '{ids[2]}', '{ids[3]}', '{ids[4]}');
            UPDATE public."MessagingOutboxMessages" SET "DispatchedAtUtc" = NULL, "ParkedAtUtc" = now() - interval '30 days'
            WHERE "Id" = '{ids[5]}';
            INSERT INTO public."MessagingInboxRecords" ("MessageId", "Endpoint", "ProcessedAtUtc") VALUES
                ('{Guid.NewGuid()}', 'tenantwright-a', now() - interval '49 hours'),
                ('{Guid.NewGuid()}', 'tenantwright-a', now() - interval '49 hours'),
                ('{Guid.NewGuid()}', 'tenantwright-b', now() - interval '49 hours'),
                ('{Guid.NewGuid()}', 'tenantwright-a', now() - interval '47 hours'),
                ('{Guid.NewGuid()}', 'tenantwright-b', now());
            """);

        Assert.Equal(5, await producer.Dispatcher.DeleteExpiredDispatchedAsync(CancellationToken.None));
        Assert.Equal(3, await producer.InboxRetention.DeleteExpiredAsync(CancellationToken.None));

        var remaining = await producer.OutboxAsync();
        Assert.Equal(ids.Skip(5).Order(), remaining.Select(row => row.Id).Order());
        Assert.Contains(remaining, row => row.ParkedAtUtc is not null);
        await using var read = producer.ReadContext();
        Assert.Equal(2, await read.InboxRecords.CountAsync());
    }

    [Fact]
    public async Task Concurrent_delivery_of_one_message_should_run_the_handler_once()
    {
        await using var consumerFixture = await CreateEndToEndAsync(holdConsumerTransaction: TimeSpan.FromMilliseconds(300));
        var (producer, consumer, probe) = consumerFixture;
        var tenantId = Guid.NewGuid();
        var key = Guid.NewGuid();
        await producer.InUnitOfWorkAsync((services, _) => NeutralMessagingHost.PublishAsync(services, tenantId, key),
            tenantId: tenantId);
        var row = Assert.Single(await producer.OutboxAsync());
        var duplicate = new TenantWrightTransportMessage(
            row.Id,
            row.ContractIdentity,
            row.Kind,
            row.Destination,
            row.Payload,
            System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(row.Headers)!);

        // Eight simultaneous broker deliveries of the same message id.
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            Task.Run(() => producer.Transport.DispatchAsync(duplicate, CancellationToken.None))));

        Assert.Equal(1, probe.Invocations);
        await using var read = consumer.ReadContext();
        Assert.Equal(1, await read.ConsumerEffects.CountAsync(effect => effect.MessageId == key));
        var inbox = Assert.Single(await read.InboxRecords.AsNoTracking().ToListAsync());
        Assert.Equal(row.Id, inbox.MessageId);
        Assert.Equal(NeutralMessagingTopology.Endpoint, inbox.Endpoint);
        Assert.Single(await consumer.OutboxAsync());
    }

    [Fact]
    public async Task End_to_end_delivery_should_wake_the_dispatcher_on_commit_restore_the_tenant_and_commit_effects_atomically()
    {
        await using var endToEnd = await CreateEndToEndAsync(pollInterval: "00:00:30");
        var (producer, consumer, probe) = endToEnd;
        var tenantId = Guid.NewGuid();
        var key = Guid.NewGuid();
        using var stop = new CancellationTokenSource();
        await producer.Dispatcher.StartAsync(stop.Token);
        try
        {
            // Let the dispatcher go idle on its 30-second poll so only the commit wake-up can deliver promptly.
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            await producer.InUnitOfWorkAsync(async (services, dbContext) =>
            {
                dbContext.ProducerWrites.Add(new ProducerWrite(key));
                await NeutralMessagingHost.PublishAsync(services, tenantId, key);
            }, tenantId: tenantId);

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (probe.Invocations == 0 && DateTime.UtcNow < deadline)
                await Task.Delay(TimeSpan.FromMilliseconds(50));
            Assert.Equal(1, probe.Invocations);
        }
        finally
        {
            await stop.CancelAsync();
            await producer.Dispatcher.StopAsync(CancellationToken.None);
        }

        Assert.Equal(tenantId, Assert.Single(probe.ObservedTenants));
        Assert.NotNull(Assert.Single(await producer.OutboxAsync()).DispatchedAtUtc);
        await using var read = consumer.ReadContext();
        Assert.True(await read.ConsumerEffects.AnyAsync(effect => effect.MessageId == key));
        Assert.Single(await read.InboxRecords.ToListAsync());

        // The follow-up event the handler sent landed in the consumer's outbox in the same transaction.
        var followUp = Assert.Single(await consumer.OutboxAsync());
        Assert.Equal("Shared.EventContracts.FeatureValuesChangedTenantEto", followUp.ContractIdentity);
        Assert.Equal(tenantId, followUp.TenantId);
        Assert.Contains(NeutralMessagingTopology.Consumer, followUp.Headers, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_handler_should_roll_back_its_effect_inbox_record_and_follow_up_then_succeed_on_retry()
    {
        await using var endToEnd = await CreateEndToEndAsync();
        var (producer, consumer, probe) = endToEnd;
        var failures = 0;
        probe.FailWhen = _ => Interlocked.Increment(ref failures) == 1;
        var tenantId = Guid.NewGuid();
        var key = Guid.NewGuid();
        await producer.InUnitOfWorkAsync((services, _) => NeutralMessagingHost.PublishAsync(services, tenantId, key),
            tenantId: tenantId);

        Assert.Equal(1, await producer.Dispatcher.DispatchBatchAsync(CancellationToken.None));
        await using (var read = consumer.ReadContext())
        {
            Assert.False(await read.ConsumerEffects.AnyAsync());
            Assert.False(await read.InboxRecords.AnyAsync());
        }
        Assert.Empty(await consumer.OutboxAsync());
        Assert.Null(Assert.Single(await producer.OutboxAsync()).DispatchedAtUtc);

        await Task.Delay(TimeSpan.FromMilliseconds(100));
        await producer.DrainAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(2, probe.Invocations);
        await using (var read = consumer.ReadContext())
        {
            Assert.Equal(1, await read.ConsumerEffects.CountAsync(effect => effect.MessageId == key));
            Assert.Equal(1, await read.InboxRecords.CountAsync());
        }
        Assert.Single(await consumer.OutboxAsync());
        Assert.NotNull(Assert.Single(await producer.OutboxAsync()).DispatchedAtUtc);
    }

    [Fact]
    public async Task Forged_origin_written_straight_to_the_outbox_should_be_rejected_by_the_consumer()
    {
        await using var endToEnd = await CreateEndToEndAsync();
        var (producer, consumer, probe) = endToEnd;
        var tenantId = Guid.NewGuid();
        await producer.InUnitOfWorkAsync((services, _) => NeutralMessagingHost.PublishAsync(services, tenantId, Guid.NewGuid()),
            tenantId: tenantId);
        var row = Assert.Single(await producer.OutboxAsync());
        var headers = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(row.Headers)!;
        headers[TenantWrightTrustHeaders.TenantId] = Guid.NewGuid().ToString("D");
        var forged = new TenantWrightTransportMessage(row.Id, row.ContractIdentity, row.Kind, row.Destination, row.Payload, headers);

        var exception = await Assert.ThrowsAsync<MessagingSecurityException>(() =>
            producer.Transport.DispatchAsync(forged, CancellationToken.None));

        Assert.Equal(MessagingSecurityException.InvalidSignature, exception.Message);
        Assert.Equal(0, probe.Invocations);
        await using var read = consumer.ReadContext();
        Assert.False(await read.InboxRecords.AnyAsync());
    }

    private async Task<NeutralMessagingHost> CreateProducerAsync(string? connectionString = null) =>
        NeutralMessagingHost.Create(
            connectionString ?? await postgres.CreateDatabaseAsync(),
            _keys,
            NeutralMessagingTopology.Producer,
            consumes: false);

    private async Task<EndToEnd> CreateEndToEndAsync(TimeSpan? holdConsumerTransaction = null, string? pollInterval = null)
    {
        var probe = new ConsumerProbe { HoldTransactionFor = holdConsumerTransaction ?? TimeSpan.Zero };
        var consumer = NeutralMessagingHost.Create(
            await postgres.CreateDatabaseAsync(), _keys, NeutralMessagingTopology.Consumer, consumes: true, probe: probe);
        var transport = new LoopbackTransport(NeutralMessagingTopology.Producer)
        {
            Consumer = consumer,
            ConsumerEndpoint = NeutralMessagingTopology.Endpoint
        };
        var settings = pollInterval is null
            ? null
            : new Dictionary<string, string?> { [$"{TenantWrightOutboxOptions.SectionName}:PollInterval"] = pollInterval };
        var producer = NeutralMessagingHost.Create(
            await postgres.CreateDatabaseAsync(), _keys, NeutralMessagingTopology.Producer, consumes: false, settings,
            transport: transport);
        return new EndToEnd(producer, consumer, probe);
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private sealed record EndToEnd(NeutralMessagingHost Producer, NeutralMessagingHost Consumer, ConsumerProbe Probe)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Producer.DisposeAsync();
            await Consumer.DisposeAsync();
        }
    }
}
