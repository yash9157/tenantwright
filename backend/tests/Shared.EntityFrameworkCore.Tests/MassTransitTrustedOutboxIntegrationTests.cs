using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shared.EntityFrameworkCore.Messaging;
using Shared.EntityFrameworkCore.Tests.TestDoubles;
using Shared.EventBus.MassTransit;
using Shared.EventBus.Messaging;
using Shared.EventContracts;
using Shared.MultiTenancy;
using Shared.Permissions;
using Shared.Permissions.Caching;
using Shared.Uow.Abstractions;
using Xunit;

namespace Shared.EntityFrameworkCore.Tests;

/// <summary>
/// The TenantWright trust filters exercised through the real EF bus outbox, using the composition a service host uses.
/// </summary>
/// <remarks>
/// Separate from <c>MassTransitPostgresOutboxIntegrationTests</c>, which proves generic MassTransit outbox atomicity
/// without distributed-contract metadata or trust filters. Both suites share one infrastructure owner in
/// <c>TestDoubles/MessagingIntegrationInfrastructure.cs</c>.
///
/// Producer and consumer are separate hosts on purpose. The production topology names
/// <c>tenantwright-permissions</c> as the only permitted origin of <c>PermissionGrantsChangedTenantEto</c> and gives every
/// approved principal its own permission-cache queue, so a single host consuming its own event would prove neither the
/// origin rule nor the fan-out.
/// </remarks>
[Collection(TrustedMessagingCollection.Name)]
public sealed class MassTransitTrustedOutboxIntegrationTests
{
    /// <summary>
    /// Composition smoke gate: the module graph builds, initializes, and resolves the real unit-of-work and
    /// permission-cache services for both hosts, and both buses start and stop cleanly.
    /// </summary>
    /// <remarks>
    /// Deliberately publishes nothing. Composing <c>AddTenantWrightMessagingHost</c> against the real Shared.Uow,
    /// Shared.Caching and Shared.Permissions modules is the part most likely to fail, and a failure here would
    /// otherwise surface as an unexplained timeout inside a much larger delivery test.
    /// </remarks>
    [MessagingInfrastructureFact]
    [Trait("Category", "RabbitMqIntegration")]
    [Trait("Category", "PostgresIntegration")]
    public async Task Trusted_messaging_hosts_should_compose_from_the_real_module_graph()
    {
        using var environment = new DevelopmentEnvironmentScope();
        await using var infrastructure = await DisposableMessagingInfrastructure.StartAsync(includeValkey: true);
        using var keys = new MessagingTrustKeys(
            TenantWrightMessagingTopology.PermissionsPrincipal,
            TenantWrightMessagingTopology.IdentityPrincipal);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));

        // One broker user per principal, because the validator requires username == producer principal.
        await infrastructure.ProvisionPrincipalsAsync(
        [
            TenantWrightMessagingTopology.PermissionsPrincipal,
            TenantWrightMessagingTopology.IdentityPrincipal
        ]);

        await using var producer = await TrustedMessagingHost.CreateAsync(
            infrastructure, keys, TenantWrightMessagingTopology.PermissionsPrincipal, "trusted-producer");
        await using var consumer = await TrustedMessagingHost.CreateAsync(
            infrastructure, keys, TenantWrightMessagingTopology.IdentityPrincipal, "trusted-consumer");

        await CreateSchemaAsync(producer, timeout.Token);
        await producer.StartAsync(timeout.Token);
        await consumer.StartAsync(timeout.Token);

        AssertRealUnitOfWorkComposition(producer);
        AssertRealUnitOfWorkComposition(consumer);
        AssertPermissionCacheConsumerResolves(consumer);
        AssertContextRequiresAUnitOfWork(producer);
        AssertExecutorIsIntercepted(producer);

        // Lower-level composition check retained: the manager alone establishes a usable unit of work.
        await AssertUnitOfWorkProvidesTheContextAsync(producer, timeout.Token);

        // The path the delivery proofs use.
        await AssertExecutorCommitsAsync(producer, timeout.Token);
        await AssertExecutorRollsBackAndPropagatesAsync(producer, timeout.Token);
        await AssertExecutorCancellationDoesNotCommitAsync(producer);

        await producer.StopAsync(timeout.Token);
        await consumer.StopAsync(timeout.Token);
    }

    /// <summary>
    /// Proves the real unit-of-work services are present, rather than a no-op stand-in.
    /// </summary>
    /// <remarks>
    /// <c>UnitOfWorkExecutor</c> and <c>UnitOfWorkDbContextProvider&lt;&gt;</c> have no explicit registration anywhere:
    /// they are discovered by Shared.Modularity conventional scanning. Asserting their concrete types is what
    /// distinguishes a real module graph from a hand-assembled service collection.
    /// </remarks>
    private static void AssertRealUnitOfWorkComposition(TrustedMessagingHost host)
    {
        using var scope = host.Services.CreateScope();
        var services = scope.ServiceProvider;

        var manager = services.GetRequiredService<IUnitOfWorkManager>();
        var executor = services.GetRequiredService<IUnitOfWorkExecutor>();
        var contextProvider = services.GetRequiredService<IDbContextProvider<TrustedMessagingDbContext>>();

        Assert.Equal("UnitOfWorkManager", manager.GetType().Name);
        Assert.Equal("UnitOfWorkDbContextProvider`1", contextProvider.GetType().Name);

        // The executor is deliberately not compared to a concrete type name here: under the Autofac provider factory
        // production hosts use, it resolves as a Castle proxy. AssertExecutorIsIntercepted owns that assertion.
        Assert.NotNull(executor);
    }

    /// <summary>
    /// Resolves the production permission-cache consumer and its whole dependency chain, including the hybrid stamp
    /// cache over the in-memory provider.
    /// </summary>
    private static void AssertPermissionCacheConsumerResolves(TrustedMessagingHost host)
    {
        using var scope = host.Services.CreateScope();
        var services = scope.ServiceProvider;

        Assert.NotNull(services.GetRequiredService<TenantWrightMessageConsumer<PermissionGrantsChangedTenantEto>>());
        Assert.IsType<PermissionGrantChangedEventHandler>(
            services.GetRequiredService<ITenantWrightMessageHandler<PermissionGrantsChangedTenantEto>>());
        Assert.NotNull(services.GetRequiredService<PermissionProviderStampHybridCache>());
        Assert.NotNull(services.GetRequiredService<Microsoft.Extensions.Caching.Distributed.IDistributedCache>());

        // The duplicate-suppression proof observes the handler's cache write, so that observation point has to actually
        // be in the graph. Asserting it here keeps a silent composition regression from turning into a four-minute
        // delivery timeout with an empty effect count.
        Assert.IsType<RecordingPermissionProviderStampCache>(
            services.GetRequiredService<PermissionProviderStampHybridCache>());
    }

    /// <summary>
    /// Enters a unit of work first, then obtains the context inside it, and completes before leaving.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="IUnitOfWorkManager"/> directly rather than
    /// <c>IUnitOfWorkExecutor.ExecuteTransactionalAsync</c>. <c>UnitOfWorkExecutor</c> carries its behavior on a
    /// <c>[UnitOfWork]</c> attribute, which is applied by the <c>UnitOfWorkInterceptor</c> under Autofac interception —
    /// the container a service host builds. Resolved from a plain Microsoft DI container the type is present but the
    /// attribute is inert, so the executor would return without ever beginning a unit of work. Recorded in the task
    /// record: the full delivery test needs the Autofac provider factory to satisfy the
    /// <c>ExecuteTransactionalAsync</c> requirement faithfully.
    ///
    /// The boundary being proven here is the one that matters either way: no concrete context exists before the unit of
    /// work, and the context is obtained from the provider inside it.
    /// </remarks>
    private static async Task AssertUnitOfWorkProvidesTheContextAsync(
        TrustedMessagingHost host,
        CancellationToken cancellationToken)
    {
        using var scope = host.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();

        using var unitOfWork = manager.Begin(
            new Shared.Uow.Options.UnitOfWorkOptions { IsTransactional = true },
            requiresNew: true);

        var provider = scope.ServiceProvider.GetRequiredService<IDbContextProvider<TrustedMessagingDbContext>>();
        var dbContext = await provider.GetDbContextAsync();

        // A real transaction exists, so this is the boundary the delivery proofs will rely on.
        Assert.NotNull(dbContext.Database.CurrentTransaction);
        Assert.Equal(nameof(TrustedMessagingDbContext), dbContext.GetType().Name);

        await unitOfWork.CompleteAsync(cancellationToken);
    }

    /// <summary>
    /// Creates the schema once for the run.
    /// </summary>
    /// <remarks>
    /// Resolves the context directly rather than through <c>IDbContextProvider</c>, which refuses to hand out a context
    /// outside a unit of work — correctly, and that refusal is asserted separately. Schema creation is DDL setup rather
    /// than behavior under test, and <c>EnsureCreatedAsync</c> has no business running inside a business transaction.
    /// </remarks>
    /// <summary>
    /// The executor resolves through its interface and is a Castle proxy, so its <c>[UnitOfWork]</c> attribute is
    /// actually applied.
    /// </summary>
    /// <remarks>
    /// Without interception the type still resolves and every method still returns, but no unit of work is ever begun —
    /// a failure mode that looks like success. Asserting the proxy is what distinguishes the two.
    /// </remarks>
    private static void AssertExecutorIsIntercepted(TrustedMessagingHost host)
    {
        using var scope = host.Services.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<IUnitOfWorkExecutor>();

        var implementationType = executor.GetType();
        Assert.NotEqual(typeof(Shared.Uow.Implementations.UnitOfWorkExecutor), implementationType);
        Assert.Contains("Proxy", implementationType.Name, StringComparison.Ordinal);
        Assert.True(
            typeof(IUnitOfWorkExecutor).IsAssignableFrom(implementationType),
            "The intercepted executor must still satisfy its interface.");
    }

    /// <summary>
    /// <c>ExecuteTransactionalAsync</c> establishes a real ambient unit of work with a live PostgreSQL transaction, and
    /// a successful delegate commits.
    /// </summary>
    private static async Task AssertExecutorCommitsAsync(
        TrustedMessagingHost host,
        CancellationToken cancellationToken)
    {
        var messageId = Guid.NewGuid();

        using (var scope = host.Services.CreateScope())
        {
            var executor = scope.ServiceProvider.GetRequiredService<IUnitOfWorkExecutor>();
            var manager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();

            // No ambient unit of work before entering the executor.
            Assert.Null(manager.Current);

            await executor.ExecuteTransactionalAsync(
                async _ =>
                {
                    Assert.NotNull(manager.Current);

                    var provider = scope.ServiceProvider
                        .GetRequiredService<IDbContextProvider<TrustedMessagingDbContext>>();
                    var dbContext = await provider.GetDbContextAsync();

                    Assert.NotNull(dbContext.Database.CurrentTransaction);
                    dbContext.ProducerWrites.Add(new ProducerWrite(messageId));
                },
                cancellationToken);

            // The ambient unit of work is gone once the executor returns.
            Assert.Null(manager.Current);
        }

        Assert.True(await ProducerWriteExistsAsync(host, messageId, cancellationToken));
    }

    /// <summary>A thrown exception rolls the transaction back and propagates to the caller.</summary>
    private static async Task AssertExecutorRollsBackAndPropagatesAsync(
        TrustedMessagingHost host,
        CancellationToken cancellationToken)
    {
        var messageId = Guid.NewGuid();
        var expected = $"rollback-{messageId:N}";

        using (var scope = host.Services.CreateScope())
        {
            var executor = scope.ServiceProvider.GetRequiredService<IUnitOfWorkExecutor>();

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                executor.ExecuteTransactionalAsync(
                    async _ =>
                    {
                        var provider = scope.ServiceProvider
                            .GetRequiredService<IDbContextProvider<TrustedMessagingDbContext>>();
                        var dbContext = await provider.GetDbContextAsync();
                        dbContext.ProducerWrites.Add(new ProducerWrite(messageId));
                        await dbContext.SaveChangesAsync(cancellationToken);

                        throw new InvalidOperationException(expected);
                    },
                    cancellationToken));

            Assert.Equal(expected, exception.Message);
        }

        // Written and saved inside the transaction, then rolled back, so it must not be visible.
        Assert.False(await ProducerWriteExistsAsync(host, messageId, cancellationToken));
    }

    /// <summary>Cancellation propagates and commits nothing.</summary>
    private static async Task AssertExecutorCancellationDoesNotCommitAsync(TrustedMessagingHost host)
    {
        var messageId = Guid.NewGuid();

        using (var scope = host.Services.CreateScope())
        {
            var executor = scope.ServiceProvider.GetRequiredService<IUnitOfWorkExecutor>();
            using var cancelled = new CancellationTokenSource();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                executor.ExecuteTransactionalAsync(
                    async token =>
                    {
                        var provider = scope.ServiceProvider
                            .GetRequiredService<IDbContextProvider<TrustedMessagingDbContext>>();
                        var dbContext = await provider.GetDbContextAsync();
                        dbContext.ProducerWrites.Add(new ProducerWrite(messageId));

                        await cancelled.CancelAsync();
                        token.ThrowIfCancellationRequested();
                    },
                    cancelled.Token));
        }

        Assert.False(await ProducerWriteExistsAsync(host, messageId, CancellationToken.None));
    }

    /// <summary>
    /// Reads committed state on its own connection, outside any unit of work, so the assertion cannot observe
    /// uncommitted rows.
    /// </summary>
    private static async Task<bool> ProducerWriteExistsAsync(
        TrustedMessagingHost host,
        Guid messageId,
        CancellationToken cancellationToken)
    {
        using var scope = host.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TrustedMessagingDbContext>();
        return await dbContext.ProducerWrites.AnyAsync(write => write.MessageId == messageId, cancellationToken);
    }

    private static async Task CreateSchemaAsync(TrustedMessagingHost host, CancellationToken cancellationToken)
    {
        using var scope = host.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TrustedMessagingDbContext>();
        await dbContext.Database.EnsureCreatedAsync(cancellationToken);
    }

    /// <summary>
    /// The provider refuses a context outside a unit of work. Asserted explicitly, because the delivery proofs depend on
    /// that boundary being real rather than incidental.
    /// </summary>
    private static void AssertContextRequiresAUnitOfWork(TrustedMessagingHost host)
    {
        using var scope = host.Services.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<IDbContextProvider<TrustedMessagingDbContext>>();

        var exception = Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetDbContextAsync()).Result;

        Assert.Contains("unit of work", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// The recorded Option B delivery proofs: a real permission-grant change published by the
/// <c>tenantwright-permissions</c> host through the EF bus outbox and consumed by the production
/// <c>tenantwright-identity-permission-cache-v1</c> endpoint.
/// </summary>
/// <remarks>
/// One test method rather than eleven, because every proof shares one expensive fixture (PostgreSQL, RabbitMQ, Valkey,
/// two composed hosts) and several proofs are ordered: the outage proof must observe pending state before the recovery
/// proof drains it. The proofs are sequenced explicitly and each has its own named assertion.
/// </remarks>
[Collection(TrustedMessagingCollection.Name)]
public sealed class MassTransitTrustedDeliveryIntegrationTests
{
    /// <summary>How long to let the outbox delivery service and broker settle before asserting an absence.</summary>
    private static readonly TimeSpan SettleWindow = TimeSpan.FromSeconds(8);

    /// <summary>Delivery is asynchronous through the outbox sweeper, so positive waits are generous.</summary>
    private static readonly TimeSpan DeliveryWindow = TimeSpan.FromMinutes(2);

    private const string ProviderName = "TrustedMessagingTest";

    [MessagingInfrastructureFact]
    [Trait("Category", "RabbitMqIntegration")]
    [Trait("Category", "PostgresIntegration")]
    public async Task Trusted_outbox_should_deliver_permission_cache_invalidation_from_permissions_to_identity()
    {
        using var environment = new DevelopmentEnvironmentScope();
        await using var infrastructure = await DisposableMessagingInfrastructure.StartAsync(includeValkey: true);
        using var keys = new MessagingTrustKeys(
            TenantWrightMessagingTopology.PermissionsPrincipal,
            TenantWrightMessagingTopology.IdentityPrincipal);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));

        await infrastructure.ProvisionPrincipalsAsync(
        [
            TenantWrightMessagingTopology.PermissionsPrincipal,
            TenantWrightMessagingTopology.IdentityPrincipal
        ]);

        // Production services own separate databases. Keeping these hosts on one database would let either bus-outbox
        // delivery worker claim the other service's rows and publish them under the wrong broker identity.
        var producerConnectionString = await infrastructure.CreatePostgresDatabaseAsync();
        var consumerConnectionString = await infrastructure.CreatePostgresDatabaseAsync();
        await using var producer = await TrustedMessagingHost.CreateAsync(
            infrastructure,
            keys,
            TenantWrightMessagingTopology.PermissionsPrincipal,
            "delivery-producer",
            producerConnectionString);
        await using var consumer = await TrustedMessagingHost.CreateAsync(
            infrastructure,
            keys,
            TenantWrightMessagingTopology.IdentityPrincipal,
            "delivery-consumer",
            consumerConnectionString);

        // The producer owns outbox rows in the Permissions database; the consumer owns inbox records in the Identity
        // database.
        await CreateSchemaAsync(producer, timeout.Token);
        await CreateSchemaAsync(consumer, timeout.Token);

        var observer = new PermissionCacheConsumeObserver();
        using var observerHandle = consumer.ConnectConsumeObserver(observer);

        await producer.StartAsync(timeout.Token);
        await consumer.StartAsync(timeout.Token);

        var tenantId = Guid.NewGuid();

        // Proofs 1 and 2: origin and endpoint identity come from production topology, not from test constants.
        AssertProductionOriginAndEndpoint();

        // Proofs 3 and 4.
        await AssertCommitDeliversOnceWithTrustMetadataAsync(
            infrastructure, producer, observer, tenantId, timeout.Token);

        // Proof 5.
        await AssertRollbackDeliversNothingAsync(producer, observer, tenantId, timeout.Token);

        // Proof 6, both halves.
        await AssertMissingTenantWritesNoOutboxMessageAsync(producer, timeout.Token);
        await AssertMismatchedTenantWritesNoOutboxMessageAsync(producer, tenantId, timeout.Token);

        // Proofs 7 and 8 are one ordered sequence: observe pending state during the outage, then recovery.
        await AssertOutagePreservesPendingOutboxThenRecoversAsync(
            infrastructure, producer, observer, tenantId, timeout.Token);

        // Proof 9.
        await AssertInboxSuppressesDuplicateEffectAsync(
            infrastructure, producer, consumer, observer, tenantId, timeout.Token);

        // Proof 10.
        await AssertCancellationLeavesNoOutboxMessageAsync(producer, tenantId);

        Assert.Empty(observer.Faults);

        // Proof 11: both hosts stop cleanly; container removal is asserted by the fixture's disposal.
        await producer.StopAsync(timeout.Token);
        await consumer.StopAsync(timeout.Token);
    }

    /// <summary>
    /// Proof 1 and 2. The only permitted origin of <c>PermissionGrantsChangedTenantEto</c> is
    /// <c>tenantwright-permissions</c>, and the Identity permission-cache queue name is the production one.
    /// </summary>
    private static void AssertProductionOriginAndEndpoint()
    {
        var route = TenantWrightMessagingTopology.CreateCatalog(null, null, [])
            .GetRoute(typeof(PermissionGrantsChangedTenantEto));

        Assert.Equal(
            [TenantWrightMessagingTopology.PermissionsPrincipal],
            route.AllowedOriginPrincipals.OrderBy(principal => principal, StringComparer.Ordinal));

        Assert.Equal(
            "tenantwright-identity-permission-cache-v1",
            TenantWrightMessagingTopology.PermissionCacheEndpoint(TenantWrightMessagingTopology.IdentityPrincipal));
    }

    /// <summary>
    /// Proofs 3 and 4. A committed unit of work delivers exactly once, and the outbox row carries the tenant, origin and
    /// custody headers the trust filters add.
    /// </summary>
    private static async Task AssertCommitDeliversOnceWithTrustMetadataAsync(
        DisposableMessagingInfrastructure infrastructure,
        TrustedMessagingHost producer,
        PermissionCacheConsumeObserver observer,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var providerKey = NewProviderKey("commit");

        var headers = await PublishInsideCommittedUnitOfWorkAsync(
            producer, tenantId, providerKey, captureOutboxHeaders: true, cancellationToken);

        // Proof 3: metadata is persisted in the outbox row, before any broker involvement.
        Assert.NotNull(headers);
        AssertTrustMetadata(headers!, tenantId);

        // Proof 4: exactly once. The wait succeeds on the first delivery; the settle window then proves no second one.
        var delivered = await observer.WaitForCountAsync(providerKey, 1, DeliveryWindow);
        if (!delivered)
        {
            var queueState = await infrastructure.ListQueuesAsync();
            var pendingOutbox = await CountPendingOutboxMessagesAsync(producer, providerKey, cancellationToken);
            Assert.Fail(
                $"The Identity permission-cache endpoint never consumed {providerKey}. " +
                $"pending-outbox={pendingOutbox}; queues={queueState}; " +
                $"consume-faults={string.Join(" | ", observer.Faults.Select(fault => fault.Message))}");
        }
        Assert.True(
            await observer.StaysAtAsync(providerKey, 1, SettleWindow),
            $"{providerKey} was delivered more than once.");

        // The message reached the production endpoint, not some other queue.
        var endpoint = observer.EndpointFor(providerKey);
        Assert.NotNull(endpoint);
        Assert.EndsWith(
            TenantWrightMessagingTopology.PermissionCacheEndpoint(TenantWrightMessagingTopology.IdentityPrincipal),
            endpoint!.AbsolutePath,
            StringComparison.Ordinal);

        Assert.Equal(tenantId, observer.TenantFor(providerKey));
    }

    /// <summary>Proof 5. A rolled-back unit of work leaves no outbox row and delivers nothing.</summary>
    private static async Task AssertRollbackDeliversNothingAsync(
        TrustedMessagingHost producer,
        PermissionCacheConsumeObserver observer,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var providerKey = NewProviderKey("rollback");
        var expected = $"rollback-{providerKey}";

        using var scope = producer.Services.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<IUnitOfWorkExecutor>();
        var currentTenant = scope.ServiceProvider.GetRequiredService<ICurrentTenant>();

        using (currentTenant.Change(tenantId))
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                executor.ExecuteTransactionalAsync(
                    async token =>
                    {
                        await PublishAsync(scope.ServiceProvider, tenantId, providerKey, token);
                        throw new InvalidOperationException(expected);
                    },
                    cancellationToken));

            Assert.Equal(expected, exception.Message);
        }

        Assert.Equal(0, await CountOutboxMessagesAsync(producer, providerKey, cancellationToken));
        Assert.True(
            await observer.StaysAtAsync(providerKey, 0, SettleWindow),
            "A rolled-back publish was delivered.");
    }

    /// <summary>
    /// Proof 6, first half. A tenant-scoped contract published with no ambient tenant is refused before it can reach
    /// the outbox.
    /// </summary>
    private static async Task AssertMissingTenantWritesNoOutboxMessageAsync(
        TrustedMessagingHost producer,
        CancellationToken cancellationToken)
    {
        var providerKey = NewProviderKey("no-tenant");

        using var scope = producer.Services.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<IUnitOfWorkExecutor>();

        // No ICurrentTenant.Change and a null TenantId on the message.
        await Assert.ThrowsAnyAsync<Exception>(() =>
            executor.ExecuteTransactionalAsync(
                token => PublishAsync(scope.ServiceProvider, tenantId: null, providerKey, token),
                cancellationToken));

        Assert.Equal(0, await CountOutboxMessagesAsync(producer, providerKey, cancellationToken));
    }

    /// <summary>
    /// Proof 6, second half. A message whose tenant disagrees with the ambient tenant is refused; a mismatch must never
    /// become an outbox row, because that is how one tenant's invalidation reaches another's cache.
    /// </summary>
    private static async Task AssertMismatchedTenantWritesNoOutboxMessageAsync(
        TrustedMessagingHost producer,
        Guid ambientTenantId,
        CancellationToken cancellationToken)
    {
        var providerKey = NewProviderKey("wrong-tenant");
        var otherTenantId = Guid.NewGuid();

        using var scope = producer.Services.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<IUnitOfWorkExecutor>();
        var currentTenant = scope.ServiceProvider.GetRequiredService<ICurrentTenant>();

        using (currentTenant.Change(ambientTenantId))
        {
            await Assert.ThrowsAnyAsync<Exception>(() =>
                executor.ExecuteTransactionalAsync(
                    token => PublishAsync(scope.ServiceProvider, otherTenantId, providerKey, token),
                    cancellationToken));
        }

        Assert.Equal(0, await CountOutboxMessagesAsync(producer, providerKey, cancellationToken));
    }

    /// <summary>
    /// Proofs 7 and 8. With the broker down the commit still succeeds and the outbox row stays pending; once the broker
    /// returns, the sweeper delivers it exactly once.
    /// </summary>
    /// <remarks>
    /// The broker is killed before the transaction opens and restarted only after it commits, so the database
    /// transaction is never held open across a broker wait. That ordering is the point of the bus outbox.
    /// </remarks>
    private static async Task AssertOutagePreservesPendingOutboxThenRecoversAsync(
        DisposableMessagingInfrastructure infrastructure,
        TrustedMessagingHost producer,
        PermissionCacheConsumeObserver observer,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var providerKey = NewProviderKey("outage");

        await infrastructure.KillRabbitAsync();

        // Commits against PostgreSQL alone. No broker round-trip happens inside the unit of work.
        await PublishInsideCommittedUnitOfWorkAsync(
            producer, tenantId, providerKey, captureOutboxHeaders: false, cancellationToken);

        // Proof 7: the commit succeeded and the message is still sitting in the outbox as undelivered work. The
        // dispatcher marks a row dispatched only after the broker confirms it, so an unmarked row is the pending state.
        Assert.Equal(1, await CountPendingOutboxMessagesAsync(producer, providerKey, cancellationToken));
        Assert.True(
            await observer.StaysAtAsync(providerKey, 0, SettleWindow),
            "Something was delivered while the broker was down.");
        Assert.Equal(1, await CountPendingOutboxMessagesAsync(producer, providerKey, cancellationToken));

        await infrastructure.StartRabbitAsync();

        // Proof 8: recovery delivers, exactly once.
        Assert.True(
            await observer.WaitForCountAsync(providerKey, 1, DeliveryWindow),
            $"{providerKey} was never delivered after the broker recovered.");
        Assert.True(
            await observer.StaysAtAsync(providerKey, 1, SettleWindow),
            $"{providerKey} was delivered more than once after recovery.");

        // And the outbox drains, so recovery is complete rather than merely duplicated.
        Assert.True(
            await WaitForOutboxToDrainAsync(producer, providerKey, DeliveryWindow),
            $"The outbox row for {providerKey} was never cleared after delivery.");
    }

    /// <summary>
    /// Proof 9. A second broker delivery of the same signed message reaches the endpoint, and the inbox suppresses the
    /// consumer's effect.
    /// </summary>
    /// <remarks>
    /// The duplicate is the one the outbox genuinely produces: the dispatcher is at-least-once, so a crash after the
    /// broker confirmed a message but before the row was marked dispatched sends the same message id with the same
    /// signed origin again. Resetting the dispatch mark reproduces exactly that; nothing is re-signed or re-stamped.
    /// </remarks>
    private static async Task AssertInboxSuppressesDuplicateEffectAsync(
        DisposableMessagingInfrastructure infrastructure,
        TrustedMessagingHost producer,
        TrustedMessagingHost consumer,
        PermissionCacheConsumeObserver observer,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var providerKey = NewProviderKey("duplicate");
        var effects = consumer.Services.GetRequiredService<StampCacheWriteRecorder>();
        var stampCacheKey = PermissionCacheKeys.BuildProviderKey(tenantId, ProviderName, providerKey);

        // The production endpoint binding must already exist, or a routed duplicate would prove nothing.
        var bindings = await infrastructure.ListBindingsAsync();
        Assert.Contains(IdentityPermissionCacheQueue, bindings, StringComparison.Ordinal);

        await PublishInsideCommittedUnitOfWorkAsync(
            producer, tenantId, providerKey, captureOutboxHeaders: false, cancellationToken);

        Assert.True(
            await observer.WaitForCountAsync(providerKey, 1, DeliveryWindow),
            $"{providerKey} was never delivered the first time.");
        Assert.True(
            await WaitForEffectCountAsync(effects, stampCacheKey, 1, DeliveryWindow),
            $"The Identity permission-cache effect never ran. Cache writes: {effects.Describe()}");
        Assert.True(
            await WaitForOutboxToDrainAsync(producer, providerKey, DeliveryWindow),
            $"The outbox row for {providerKey} was never marked dispatched.");

        await Task.Delay(SettleWindow, cancellationToken);
        var effectsAfterFirst = effects.CountFor(stampCacheKey);
        Assert.Equal(1, effectsAfterFirst);
        var row = await FindOutboxRowAsync(producer, providerKey, cancellationToken);
        Assert.Equal(1, await CountInboxRecordsAsync(consumer, row.Id, cancellationToken));
        var before = await infrastructure.QueueDepthsAsync();

        // Simulate the crash window: the broker confirmed, the dispatch mark was lost.
        using (var scope = producer.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<TrustedMessagingDbContext>();
            Assert.Equal(1, await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE public."MessagingOutboxMessages"
                SET "DispatchedAtUtc" = NULL, "AvailableAtUtc" = now()
                WHERE "Id" = {row.Id}
                """, cancellationToken));
        }

        // The duplicate reaches the same endpoint pipeline under the same message id.
        Assert.True(
            await observer.WaitForCountAsync(providerKey, 2, DeliveryWindow),
            "The re-dispatched message never reached the Identity consume pipeline, so suppression was not exercised.");
        await Task.Delay(SettleWindow, cancellationToken);

        var receipts = observer.ReceiptsFor(providerKey);
        Assert.True(receipts.Count >= 2, $"Expected two consume receipts, got [{string.Join(" ;; ", receipts)}].");
        Assert.All(receipts, receipt =>
        {
            Assert.Contains(row.Id.ToString("D"), receipt, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(IdentityPermissionCacheQueue, receipt, StringComparison.Ordinal);
        });

        // The guarantee: the real consumer effect did not run again, and the inbox holds one record for the endpoint.
        Assert.Equal(effectsAfterFirst, effects.CountFor(stampCacheKey));
        Assert.Equal(1, await CountInboxRecordsAsync(consumer, row.Id, cancellationToken));
        Assert.Equal(2, (await FindOutboxRowAsync(producer, providerKey, cancellationToken)).Attempts);

        // The endpoint drained it without faulting or skipping it.
        var after = await infrastructure.QueueDepthsAsync();
        Assert.Equal(0, Depth(after, IdentityPermissionCacheQueue));
        Assert.Equal(
            Depth(before, $"{IdentityPermissionCacheQueue}_error"),
            Depth(after, $"{IdentityPermissionCacheQueue}_error"));
        Assert.Equal(
            Depth(before, $"{IdentityPermissionCacheQueue}_skipped"),
            Depth(after, $"{IdentityPermissionCacheQueue}_skipped"));

        Assert.DoesNotContain(
            observer.Faults,
            fault => fault is MessagingSecurityException);
    }
    /// <summary>The production Identity permission-cache queue, from the production topology.</summary>
    private static string IdentityPermissionCacheQueue =>
        TenantWrightMessagingTopology.PermissionCacheEndpoint(TenantWrightMessagingTopology.IdentityPrincipal);

    private static int Depth(IReadOnlyDictionary<string, int> depths, string queue) =>
        depths.TryGetValue(queue, out var messages) ? messages : 0;

    private static async Task<bool> WaitForEffectCountAsync(
        StampCacheWriteRecorder effects,
        string logicalKey,
        int expected,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (effects.CountFor(logicalKey) >= expected)
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        return effects.CountFor(logicalKey) >= expected;
    }

    /// <summary>Proof 10. Cancelling inside the unit of work leaves neither business rows nor outbox rows.</summary>
    private static async Task AssertCancellationLeavesNoOutboxMessageAsync(
        TrustedMessagingHost producer,
        Guid tenantId)
    {
        var providerKey = NewProviderKey("cancelled");

        using var scope = producer.Services.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<IUnitOfWorkExecutor>();
        var currentTenant = scope.ServiceProvider.GetRequiredService<ICurrentTenant>();
        using var cancelled = new CancellationTokenSource();

        using (currentTenant.Change(tenantId))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                executor.ExecuteTransactionalAsync(
                    async token =>
                    {
                        await PublishAsync(scope.ServiceProvider, tenantId, providerKey, token);
                        await cancelled.CancelAsync();
                        token.ThrowIfCancellationRequested();
                    },
                    cancelled.Token));
        }

        Assert.Equal(0, await CountOutboxMessagesAsync(producer, providerKey, CancellationToken.None));
    }

    /// <summary>
    /// Publishes inside <c>ExecuteTransactionalAsync</c> and commits, optionally capturing the outbox row's headers
    /// while the transaction is still open.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, string>?> PublishInsideCommittedUnitOfWorkAsync(
        TrustedMessagingHost producer,
        Guid tenantId,
        string providerKey,
        bool captureOutboxHeaders,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, string>? captured = null;

        using var scope = producer.Services.CreateScope();
        var executor = scope.ServiceProvider.GetRequiredService<IUnitOfWorkExecutor>();
        var currentTenant = scope.ServiceProvider.GetRequiredService<ICurrentTenant>();

        using (currentTenant.Change(tenantId))
        {
            await executor.ExecuteTransactionalAsync(
                async token =>
                {
                    var contextProvider = scope.ServiceProvider
                        .GetRequiredService<IDbContextProvider<TrustedMessagingDbContext>>();
                    var dbContext = await contextProvider.GetDbContextAsync();

                    // A business write in the same transaction as the publish: that shared fate is what the outbox buys.
                    dbContext.ProducerWrites.Add(new ProducerWrite(Guid.NewGuid()));

                    await PublishAsync(scope.ServiceProvider, tenantId, providerKey, token);

                    if (captureOutboxHeaders)
                    {
                        AssertOutboxSharesTheUnitOfWorkDbContext(scope.ServiceProvider, dbContext);
                    }

                    await dbContext.SaveChangesAsync(token);

                    if (captureOutboxHeaders)
                    {
                        captured = await ReadOutboxHeadersAsync(dbContext, providerKey, token);
                    }
                },
                cancellationToken);
        }

        return captured;
    }

    /// <summary>
    /// Publishes through the production sender, which is what enforces the allowed-origin rule before the message
    /// reaches the publish pipeline.
    /// </summary>
    private static Task PublishAsync(
        IServiceProvider services,
        Guid? tenantId,
        string providerKey,
        CancellationToken cancellationToken)
        => services.GetRequiredService<ITenantWrightMessageSender>()
            .PublishEventAsync(BuildMessage(tenantId, providerKey), cancellationToken);

    private static PermissionGrantsChangedTenantEto BuildMessage(Guid? tenantId, string providerKey) => new()
    {
        TenantId = tenantId,
        ProviderName = ProviderName,
        ProviderKey = providerKey,
        OccurredAtUtc = DateTimeOffset.UtcNow,
        Stamp = DateTimeOffset.UtcNow.UtcTicks
    };

    /// <summary>
    /// Reads the pending outbox row's headers from inside the same transaction that wrote it, which proves the custody
    /// metadata is persisted before any broker involvement.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, string>?> ReadOutboxHeadersAsync(
        TrustedMessagingDbContext dbContext,
        string providerKey,
        CancellationToken cancellationToken)
    {
        var recent = await dbContext.Set<MessagingOutboxMessage>()
            .AsNoTracking()
            .OrderByDescending(message => message.CreatedAtUtc)
            .Take(10)
            .ToListAsync(cancellationToken);

        var row = recent.FirstOrDefault(candidate => PayloadContains(candidate, providerKey))
            ?? throw new InvalidOperationException(
                $"No outbox row carried provider key '{providerKey}'. Rows={recent.Count}: " +
                string.Join(" | ", recent.Select(candidate => $"{candidate.ContractIdentity} {candidate.CreatedAtUtc:O}")));

        return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(row.Headers);
    }

    /// <summary>
    /// Regression pin: the business write and the outbox row are tracked by the same
    /// <see cref="TrustedMessagingDbContext"/> instance, inside one transaction.
    /// </summary>
    /// <remarks>
    /// This is the assertion that fails if the sender drifts to a different context than the unit of work's. When that
    /// happened before, the outbox row was tracked by a context nobody saved, the commit succeeded, and the event was
    /// dropped with no error. Same-instance is the invariant that makes the outbox atomic.
    /// </remarks>
    private static void AssertOutboxSharesTheUnitOfWorkDbContext(
        IServiceProvider outerScope,
        TrustedMessagingDbContext unitOfWorkContext)
    {
        var manager = outerScope.GetRequiredService<IUnitOfWorkManager>();
        Assert.NotNull(manager.Current);

        var tracked = unitOfWorkContext.ChangeTracker.Entries().ToList();
        Assert.Contains(tracked, entry => entry.Entity is MessagingOutboxMessage);
        Assert.Contains(tracked, entry => entry.Entity is ProducerWrite);
        Assert.NotNull(unitOfWorkContext.Database.CurrentTransaction);
    }
    /// <summary>Proof 3's assertion: tenant, origin and custody metadata are all persisted, not added at send time.</summary>
    private static void AssertTrustMetadata(IReadOnlyDictionary<string, string> headers, Guid tenantId)
    {
        var flattened = string.Join(
            '\n',
            headers.Select(pair => $"{pair.Key}={pair.Value}"));

        Assert.Contains(tenantId.ToString("D"), flattened, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(TenantWrightMessagingTopology.PermissionsPrincipal, flattened, StringComparison.OrdinalIgnoreCase);

        // Custody: the signing key id the producing principal used.
        Assert.Contains($"{TenantWrightMessagingTopology.PermissionsPrincipal}-test-key", flattened, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PayloadContains(MessagingOutboxMessage message, string providerKey) =>
        System.Text.Encoding.UTF8.GetString(message.Payload).Contains(providerKey, StringComparison.Ordinal);

    private static async Task<List<MessagingOutboxMessage>> OutboxRowsAsync(
        TrustedMessagingHost host,
        string providerKey,
        CancellationToken cancellationToken)
    {
        using var scope = host.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TrustedMessagingDbContext>();
        var rows = await dbContext.Set<MessagingOutboxMessage>().AsNoTracking().ToListAsync(cancellationToken);
        return rows.Where(row => PayloadContains(row, providerKey)).ToList();
    }

    private static async Task<MessagingOutboxMessage> FindOutboxRowAsync(
        TrustedMessagingHost host,
        string providerKey,
        CancellationToken cancellationToken) =>
        Assert.Single(await OutboxRowsAsync(host, providerKey, cancellationToken));

    /// <summary>Every outbox row carrying the key, dispatched or not.</summary>
    private static async Task<int> CountOutboxMessagesAsync(
        TrustedMessagingHost host,
        string providerKey,
        CancellationToken cancellationToken) =>
        (await OutboxRowsAsync(host, providerKey, cancellationToken)).Count;

    /// <summary>Outbox rows carrying the key that the broker has not yet confirmed.</summary>
    private static async Task<int> CountPendingOutboxMessagesAsync(
        TrustedMessagingHost host,
        string providerKey,
        CancellationToken cancellationToken) =>
        (await OutboxRowsAsync(host, providerKey, cancellationToken)).Count(row => row.DispatchedAtUtc is null);

    /// <summary>Polls until the dispatcher has marked the row dispatched, which it does only after broker confirmation.</summary>
    private static async Task<bool> WaitForOutboxToDrainAsync(
        TrustedMessagingHost host,
        string providerKey,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await CountPendingOutboxMessagesAsync(host, providerKey, CancellationToken.None) == 0)
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }

        return await CountPendingOutboxMessagesAsync(host, providerKey, CancellationToken.None) == 0;
    }

    private static async Task<int> CountInboxRecordsAsync(
        TrustedMessagingHost host,
        Guid messageId,
        CancellationToken cancellationToken)
    {
        using var scope = host.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TrustedMessagingDbContext>();
        return await dbContext.Set<MessagingInboxRecord>()
            .CountAsync(record => record.MessageId == messageId, cancellationToken);
    }
    private static string NewProviderKey(string label) => $"trusted-{label}-{Guid.NewGuid():N}";

    private static async Task CreateSchemaAsync(TrustedMessagingHost host, CancellationToken cancellationToken)
    {
        using var scope = host.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TrustedMessagingDbContext>();
        await dbContext.Database.EnsureCreatedAsync(cancellationToken);
    }
}
