using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Shared.EventBus.MassTransit;
using Shared.EventBus.Messaging;
using Shared.EventContracts;
using Shared.MultiTenancy;
using Xunit;

namespace Shared.EventBus.Tests;

public sealed class MassTransitRabbitMqIntegrationTests
{
    private const string TestPrincipal = "tenantwright-messaging-phase2";
    private const string TestEndpoint = "tenantwright-phase2-appointments";
    private const string HostOnlyEndpoint = "tenantwright-phase2-host-only";

    /// <summary>The provider name on the tenant-scoped test contract. Never used as a message identity.</summary>
    private const string TestProviderName = "tenantwright-phase2-test-provider";

    // The positive paths publish PermissionGrantsChangedTenantEto because it is TenantRequired, implements
    // IMultiTenant, and is registered for source-generated serialization. Three identities are kept strictly apart
    // here: the tenant (TenantId, which must equal the ambient tenant), the transport message identity (MassTransit's
    // own MessageId), and the business identity used for delivery correlation (ProviderKey).
    //
    // TenantDeactivatedEto is retained deliberately, but only for the host-only paths: it is HostOnly, so the produce
    // trust filter refuses it whenever a tenant context is ambient.

    [LicensedRabbitMqFact]
    [Trait("Category", "RabbitMqIntegration")]
    public async Task Two_bus_instances_should_share_one_quorum_endpoint_for_a_tenant_scoped_event()
    {
        await using var broker = await DockerRabbitMq.StartAsync();
        using var keys = new MessagingTestKeyMaterial();
        var tracker = new RabbitMqDeliveryTracker(expectedMessages: 40);
        await using var first = BuildProvider(broker, keys, tracker, "instance-one");
        await using var second = BuildProvider(broker, keys, tracker, "instance-two");
        var firstBus = first.GetRequiredService<IBusControl>();
        var secondBus = second.GetRequiredService<IBusControl>();
        using var firstObserver = firstBus.ConnectConsumeObserver(new RabbitMqConsumeFailureObserver(tracker));
        using var secondObserver = secondBus.ConnectConsumeObserver(new RabbitMqConsumeFailureObserver(tracker));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        await firstBus.StartAsync(timeout.Token);
        await secondBus.StartAsync(timeout.Token);
        try
        {
            var health = first.GetRequiredService<HealthCheckService>();
            var readiness = await health.CheckHealthAsync(
                registration => registration.Tags.Contains("ready"), timeout.Token);
            Assert.Equal(HealthStatus.Healthy, readiness.Status);

            var tenantId = Guid.NewGuid();
            var currentTenant = first.GetRequiredService<MessagingTestCurrentTenant>();
            using (currentTenant.Change(tenantId))
            using (var scope = first.CreateScope())
            {
                var sender = new StampingTransportDispatcher(scope.ServiceProvider);
                for (var index = 0; index < tracker.ExpectedMessages; index++)
                {
                    // One tenant, many messages: the tenant is the same for every publish and the message identity
                    // is not, which is what lets the tracker prove each message arrived exactly once.
                    await sender.PublishEventAsync(new PermissionGrantsChangedTenantEto
                    {
                        TenantId = tenantId,
                        ProviderName = TestProviderName,
                        ProviderKey = Guid.NewGuid().ToString("D"),
                        Stamp = index + 1,
                        OccurredAtUtc = DateTimeOffset.UtcNow
                    }, timeout.Token);
                }
            }

            var completed = await Task.WhenAny(
                tracker.Completion,
                tracker.Faulted,
                Task.Delay(Timeout.InfiniteTimeSpan, timeout.Token));
            if (completed == tracker.Faulted)
            {
                throw new InvalidOperationException(
                    $"Trusted event delivery faulted before completion: {tracker.LastFailureCode ?? "missing"}.");
            }
            if (completed != tracker.Completion)
            {
                using var diagnosisTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var endpoint = await broker.GetQueueStateAsync(TestEndpoint, diagnosisTimeout.Token);
                var error = await broker.GetQueueStateAsync($"{TestEndpoint}_error", diagnosisTimeout.Token);
                var skipped = await broker.GetQueueStateAsync($"{TestEndpoint}_skipped", diagnosisTimeout.Token);
                throw new InvalidOperationException(
                    $"No trusted event delivery completed; handled={tracker.HandledCount}; endpoint={endpoint}; error={error}; skipped={skipped}; " +
                    $"consume failure: {tracker.LastFailureCode ?? "missing"}.");
            }
            await tracker.Completion.WaitAsync(timeout.Token);
            Assert.All(tracker.DeliveryCounts.Values, count => Assert.Equal(1, count));
            Assert.Equal(tracker.ExpectedMessages, tracker.DeliveryCounts.Count);
            Assert.True(tracker.InstanceCounts.GetValueOrDefault("instance-one") > 0);
            Assert.True(tracker.InstanceCounts.GetValueOrDefault("instance-two") > 0);
            Assert.True(await broker.IsQuorumQueueAsync(TestEndpoint, timeout.Token));
        }
        finally
        {
            var stopwatch = Stopwatch.StartNew();
            await Task.WhenAll(
                firstBus.StopAsync(timeout.Token),
                secondBus.StopAsync(timeout.Token));
            stopwatch.Stop();
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30),
                $"MassTransit shutdown exceeded the configured bound: {stopwatch.Elapsed}.");
        }
    }

    [LicensedRabbitMqFact]
    [Trait("Category", "RabbitMqIntegration")]
    public async Task Mandatory_command_send_should_reject_an_unroutable_destination_without_secret_disclosure()
    {
        await using var broker = await DockerRabbitMq.StartAsync();
        using var keys = new MessagingTestKeyMaterial();
        await using var provider = BuildUnroutableProvider(broker, keys);
        var bus = provider.GetRequiredService<IBusControl>();
        var sendObserver = new RabbitMqSendPropertyObserver();
        using var observerHandle = bus.ConnectSendObserver(sendObserver);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        await bus.StartAsync(timeout.Token);
        try
        {
            var tenantId = Guid.NewGuid();
            var currentTenant = provider.GetRequiredService<MessagingTestCurrentTenant>();
            using (currentTenant.Change(tenantId))
            using (var scope = provider.CreateScope())
            {
                var sender = new StampingTransportDispatcher(scope.ServiceProvider);
                var exception = await Record.ExceptionAsync(() => sender.SendCommandAsync(
                    new NotificationSendRequestedTenantEto
                    {
                        RequestId = Guid.NewGuid(),
                        TenantId = tenantId,
                        Channel = NotificationSendChannel.App,
                        Subject = "Test",
                        TemplateName = "test",
                        Category = "test",
                        Priority = NotificationSendPriority.Normal,
                        OccurredAtUtc = DateTime.UtcNow,
                        SourceService = TestPrincipal,
                        RespectUserPreferences = false
                    },
                    timeout.Token));
                Assert.True(
                    exception is not null,
                    $"RabbitMQ accepted an unroutable command (observed={sendObserver.Observed}, mandatory={sendObserver.Mandatory}, awaitAck={sendObserver.AwaitAck}, publishPayload={sendObserver.PublishPayload}, trustHeader={sendObserver.TrustHeader}).");
                Assert.True(sendObserver.Observed);
                Assert.True(sendObserver.Mandatory);
                Assert.True(sendObserver.AwaitAck);
                Assert.True(sendObserver.TrustHeader);
                Assert.DoesNotContain(broker.Password, exception.ToString(), StringComparison.Ordinal);
            }
        }
        finally
        {
            await bus.StopAsync(timeout.Token);
        }
    }

    [LicensedRabbitMqFact]
    [Trait("Category", "RabbitMqIntegration")]
    public async Task Queue_redelivery_should_preserve_verified_relay_and_exact_broker_death_history()
    {
        await using var broker = await DockerRabbitMq.StartAsync();
        using var keys = new MessagingTestKeyMaterial();
        var tracker = new RabbitMqDeliveryTracker(expectedMessages: 1);
        var attempts = 0;
        await using var provider = BuildProvider(broker, keys, tracker, "redelivery-instance", message =>
        {
            if (Interlocked.Increment(ref attempts) <= 3)
                throw new InvalidOperationException("fixed retryable test failure");
            tracker.Record(MessageIdentityOf(message), "redelivery-instance");
            return Task.CompletedTask;
        });
        var bus = provider.GetRequiredService<IBusControl>();
        var sendObserver = new RabbitMqSendPropertyObserver();
        using var observerHandle = bus.ConnectSendObserver(sendObserver);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        await bus.StartAsync(timeout.Token);
        try
        {
            await PublishOneAsync(provider, Guid.NewGuid(), timeout.Token);
            try
            {
                var completed = await Task.WhenAny(
                    tracker.Completion,
                    tracker.Faulted,
                    sendObserver.Faulted,
                    Task.Delay(Timeout.InfiniteTimeSpan, timeout.Token));
                if (completed == tracker.Faulted || completed == sendObserver.Faulted)
                {
                    throw new InvalidOperationException(
                        $"Delayed-redelivery transport faulted; attempts={Volatile.Read(ref attempts)}; " +
                        $"last failure={tracker.LastFailureCode ?? "missing"}; send={sendObserver.Diagnostic}.");
                }
                await tracker.Completion.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                using var diagnosisTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var endpoint = await broker.GetQueueStateAsync(TestEndpoint, diagnosisTimeout.Token);
                var error = await broker.GetQueueStateAsync($"{TestEndpoint}_error", diagnosisTimeout.Token);
                var skipped = await broker.GetQueueStateAsync($"{TestEndpoint}_skipped", diagnosisTimeout.Token);
                throw new InvalidOperationException(
                    $"Redelivery did not complete; attempts={Volatile.Read(ref attempts)}; endpoint={endpoint}; " +
                    $"error={error}; skipped={skipped}; last failure={tracker.LastFailureCode ?? "missing"}; " +
                    $"send={sendObserver.Diagnostic}.");
            }

            Assert.Equal(4, attempts);
            Assert.Single(tracker.DeliveryCounts);
        }
        finally
        {
            await bus.StopAsync(timeout.Token);
        }
    }

    [LicensedRabbitMqFact]
    [Trait("Category", "RabbitMqIntegration")]
    public async Task Security_failure_should_move_to_truthful_quorum_error_custody()
    {
        await using var broker = await DockerRabbitMq.StartAsync();
        using var keys = new MessagingTestKeyMaterial();
        var tracker = new RabbitMqDeliveryTracker(expectedMessages: 1);
        await using var provider = BuildProvider(broker, keys, tracker, "terminal-instance", _ =>
            throw new MessagingSecurityException(MessagingSecurityException.InvalidContext));
        var bus = provider.GetRequiredService<IBusControl>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        await bus.StartAsync(timeout.Token);
        try
        {
            await PublishOneAsync(provider, Guid.NewGuid(), timeout.Token);
            var terminal = await broker.WaitForTerminalMessageAsync($"{TestEndpoint}_error", timeout.Token);

            Assert.Equal("quorum", terminal.QueueType);
            Assert.Equal(TestPrincipal, terminal.UserId);
            Assert.Contains(TenantWrightTrustHeaders.TerminalId, terminal.HeaderNames);
            Assert.Contains(TenantWrightTrustHeaders.TerminalSignature, terminal.HeaderNames);
            Assert.Contains(TenantWrightTrustHeaders.TerminalClassification, terminal.HeaderNames);
        }
        finally
        {
            await bus.StopAsync(timeout.Token);
        }
    }

    [LicensedRabbitMqFact]
    [Trait("Category", "RabbitMqIntegration")]
    public async Task Broker_outage_should_degrade_readiness_and_recover_delivery()
    {
        await using var broker = await DockerRabbitMq.StartAsync();
        using var keys = new MessagingTestKeyMaterial();
        var tracker = new RabbitMqDeliveryTracker(expectedMessages: 1);
        await using var provider = BuildProvider(broker, keys, tracker, "recovery-instance");
        var bus = provider.GetRequiredService<IBusControl>();
        var health = provider.GetRequiredService<HealthCheckService>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        await bus.StartAsync(timeout.Token);
        try
        {
            await WaitForReadinessAsync(health, HealthStatus.Healthy, timeout.Token);
            await broker.KillContainerAsync();
            await WaitForReadinessAsync(health, HealthStatus.Unhealthy, timeout.Token);
            await broker.StartContainerAsync();
            using var recoveryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            await WaitForReadinessAsync(health, HealthStatus.Healthy, recoveryTimeout.Token);

            await PublishOneAsync(provider, Guid.NewGuid(), timeout.Token);
            await tracker.Completion.WaitAsync(timeout.Token);
            Assert.Single(tracker.DeliveryCounts);
        }
        finally
        {
            await broker.StartContainerAsync();
            await bus.StopAsync(timeout.Token);
        }
    }

    /// <summary>
    /// Publishes one tenant-scoped event under a real ambient tenant.
    /// </summary>
    /// <param name="messageKey">
    /// The business identity used for delivery correlation. Deliberately a separate value from the tenant: a
    /// tenant-scoped contract is refused unless its <c>TenantId</c> equals the ambient tenant, so reusing one value
    /// for both would hide that rule instead of exercising it.
    /// </param>
    /// <summary>
    /// A host-only event must not be publishable while a tenant context is ambient.
    /// </summary>
    /// <remarks>
    /// This is the rule the fixture used to violate: it published the host-only <see cref="TenantDeactivatedEto"/>
    /// inside a tenant scope and read the refusal as a product defect. A tenant-management event describes work about
    /// a tenant performed by the host, so carrying a tenant context would misattribute who acted.
    /// </remarks>
    [LicensedRabbitMqFact]
    [Trait("Category", "RabbitMqIntegration")]
    public async Task A_host_only_event_is_refused_while_a_tenant_context_is_ambient()
    {
        await using var broker = await DockerRabbitMq.StartAsync();
        using var keys = new MessagingTestKeyMaterial();
        var consumerState = new HostOnlyConsumerState(new MessagingTestCurrentTenant());
        await using var provider = BuildHostOnlyProvider(broker, keys, consumerState);
        var bus = provider.GetRequiredService<IBusControl>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        await bus.StartAsync(timeout.Token);
        try
        {
            using (consumerState.CurrentTenant.Change(Guid.NewGuid()))
            using (var scope = provider.CreateScope())
            {
                var sender = new StampingTransportDispatcher(scope.ServiceProvider);
                var exception = await Assert.ThrowsAsync<MessagingSecurityException>(() =>
                    sender.PublishEventAsync(
                        new TenantDeactivatedEto
                        {
                            TenantId = Guid.NewGuid(),
                            TenantName = "Test Tenant",
                            OccurredAtUtc = DateTime.UtcNow
                        },
                        timeout.Token));

                Assert.Equal(MessagingSecurityException.TenantMismatch, exception.Message);
            }

            // Refused before publication, so the consumer never runs. Asserted through the consumer rather than the
            // management API's queue depth: that statistic is populated asynchronously and is absent from the payload
            // until RabbitMQ has gathered it, which made an earlier form of this assertion intermittently throw.
            using var settle = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var delivered = await Task.WhenAny(
                consumerState.ObservedTenant,
                Task.Delay(Timeout.InfiniteTimeSpan, settle.Token));

            Assert.NotSame(consumerState.ObservedTenant, delivered);
        }
        finally
        {
            await bus.StopAsync(timeout.Token);
        }
    }

    /// <summary>
    /// The same host-only event publishes with no ambient tenant, and restores no tenant on consumption.
    /// </summary>
    [LicensedRabbitMqFact]
    [Trait("Category", "RabbitMqIntegration")]
    public async Task A_host_only_event_publishes_without_a_tenant_and_restores_none_on_consumption()
    {
        await using var broker = await DockerRabbitMq.StartAsync();
        using var keys = new MessagingTestKeyMaterial();
        var consumerState = new HostOnlyConsumerState(new MessagingTestCurrentTenant());
        await using var provider = BuildHostOnlyProvider(broker, keys, consumerState);
        var bus = provider.GetRequiredService<IBusControl>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        await bus.StartAsync(timeout.Token);
        try
        {
            using (var scope = provider.CreateScope())
            {
                var sender = new StampingTransportDispatcher(scope.ServiceProvider);
                await sender.PublishEventAsync(
                    new TenantDeactivatedEto
                    {
                        TenantId = Guid.NewGuid(),
                        TenantName = "Test Tenant",
                        OccurredAtUtc = DateTime.UtcNow
                    },
                    timeout.Token);
            }

            var observed = await consumerState.ObservedTenant.WaitAsync(timeout.Token);

            // The message body carries a tenant identifier as data; the transport carried no tenant context, so none
            // was restored. A host-only contract must not establish ambient tenancy in the consumer.
            Assert.Null(observed);
        }
        finally
        {
            await bus.StopAsync(timeout.Token);
        }
    }

    private static async Task PublishOneAsync(
        ServiceProvider provider,
        Guid messageKey,
        CancellationToken cancellationToken)
    {
        var tenantId = Guid.NewGuid();
        var currentTenant = provider.GetRequiredService<MessagingTestCurrentTenant>();
        using (currentTenant.Change(tenantId))
        using (var scope = provider.CreateScope())
        {
            var sender = new StampingTransportDispatcher(scope.ServiceProvider);
            await sender.PublishEventAsync(new PermissionGrantsChangedTenantEto
            {
                TenantId = tenantId,
                ProviderName = TestProviderName,
                ProviderKey = messageKey.ToString("D"),
                Stamp = 1,
                OccurredAtUtc = DateTimeOffset.UtcNow
            }, cancellationToken);
        }
    }

    /// <summary>The business identity carried by the tenant-scoped test contract.</summary>
    private static Guid MessageIdentityOf(PermissionGrantsChangedTenantEto message) =>
        Guid.Parse(message.ProviderKey);

    private static async Task WaitForReadinessAsync(
        HealthCheckService health,
        HealthStatus expected,
        CancellationToken cancellationToken)
    {
        var lastStatuses = "not-checked";
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var report = await health.CheckHealthAsync(
                    registration => registration.Tags.Contains("ready"), cancellationToken);
                lastStatuses = string.Join(",", report.Entries
                    .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                    .Select(entry =>
                    {
                        var exceptionTypes = new List<string>();
                        for (var current = entry.Value.Exception; current is not null && exceptionTypes.Count < 4; current = current.InnerException)
                            exceptionTypes.Add(current.GetType().Name);
                        return $"{entry.Key}={entry.Value.Status}[{string.Join(">", exceptionTypes)}]";
                    }));
                // The HTTP readiness boundary maps both Degraded and Unhealthy to 503.
                // MassTransit reports a disconnected-but-reconnecting bus as Degraded.
                if (expected == HealthStatus.Healthy
                        ? report.Status == HealthStatus.Healthy
                        : report.Status != HealthStatus.Healthy)
                    return;
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Readiness did not reach {expected}; last statuses: {lastStatuses}.");
        }
    }

    private static ServiceProvider BuildProvider(
        DockerRabbitMq broker,
        MessagingTestKeyMaterial keys,
        RabbitMqDeliveryTracker tracker,
        string instance,
        Func<PermissionGrantsChangedTenantEto, Task>? handler = null)
    {
        var configuration = CreateConfiguration(broker, keys);
        var currentTenant = new MessagingTestCurrentTenant();
        var consumerState = new RabbitMqConsumerState(tracker, currentTenant, instance, handler);

        // The fixture's own principal is the only permitted origin here. This is a test-local catalog; the real
        // permission-event origin rules in TenantWrightMessagingTopology are untouched.
        var catalog = new TenantWrightMessageCatalog(
            [
                new TenantWrightContractRoute(
                    typeof(PermissionGrantsChangedTenantEto),
                    TenantWrightMessageRouteKind.Event,
                    null,
                    new HashSet<string>(StringComparer.Ordinal) { TestPrincipal })
            ],
            [
                new TenantWrightEndpointRoute(
                    TestEndpoint,
                    TestPrincipal,
                    [typeof(PermissionGrantsChangedTenantEto)])
            ],
            [
                new TenantWrightReceiveEndpoint(
                    TestEndpoint,
                    TestPrincipal,
                    [typeof(PermissionGrantsChangedTenantEto)])
            ]);
        return BuildServiceProvider(
            configuration,
            currentTenant,
            catalog,
            consumerState,
            (_, receive) => receive.ConfigureError(pipe => pipe.UseFilter(new RabbitMqErrorObserver(tracker))));
    }

    /// <summary>
    /// A provider for the host-only paths. Its contract is <see cref="TenantDeactivatedEto"/>, which is
    /// <c>HostOnly</c>, so the produce filter refuses it under an ambient tenant and no tenant is restored on consume.
    /// </summary>
    private static ServiceProvider BuildHostOnlyProvider(
        DockerRabbitMq broker,
        MessagingTestKeyMaterial keys,
        HostOnlyConsumerState consumerState)
    {
        var catalog = new TenantWrightMessageCatalog(
            [
                new TenantWrightContractRoute(
                    typeof(TenantDeactivatedEto),
                    TenantWrightMessageRouteKind.Event,
                    null,
                    new HashSet<string>(StringComparer.Ordinal) { TestPrincipal })
            ],
            [
                new TenantWrightEndpointRoute(
                    HostOnlyEndpoint,
                    TestPrincipal,
                    [typeof(TenantDeactivatedEto)])
            ],
            [
                new TenantWrightReceiveEndpoint(
                    HostOnlyEndpoint,
                    TestPrincipal,
                    [typeof(TenantDeactivatedEto)])
            ]);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHealthChecks();
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(
            new MessagingTestHostEnvironment("Testing"));
        services.AddSingleton(consumerState);
        services.AddScoped<ITenantWrightMessageHandler<TenantDeactivatedEto>, HostOnlyTrackedHandler>();
        services.AddSingleton(consumerState.CurrentTenant);
        services.AddSingleton<ICurrentTenant>(consumerState.CurrentTenant);
        services.AddSingleton<IProtectedTerminalStore, MissingTerminalStore>();
        services.AddSingleton<IProtectedReplayAuthorization, RejectingReplayAuthorization>();
        services.AddSingleton<IProtectedReplayAudit, NoOpReplayAudit>();
        services.AddSingleton<IProtectedReplayIdempotencyStore, InMemoryReplayIdStore>();
        services.AddSingleton<ITenantWrightInboxTransaction>(provider => new InMemoryInboxTransaction(provider));
        services.AddSingleton<IProtectedReplayTransport, RejectingReplayTransport>();
        services.AddTenantWrightMessagingCore(CreateConfiguration(broker, keys), catalog);
        services.AddTenantWrightMassTransit(catalog);
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static ServiceProvider BuildUnroutableProvider(
        DockerRabbitMq broker,
        MessagingTestKeyMaterial keys)
    {
        var currentTenant = new MessagingTestCurrentTenant();

        // The route must name the contract this test actually sends. It previously declared TenantDeactivatedEto while
        // the test sent NotificationSendRequestedTenantEto, so the send could not have reached the transport at all and
        // the "unroutable" assertion would have passed for the wrong reason.
        var catalog = new TenantWrightMessageCatalog(
            [
                new TenantWrightContractRoute(
                    typeof(NotificationSendRequestedTenantEto),
                    TenantWrightMessageRouteKind.Command,
                    "tenantwright-phase2-unroutable",
                    new HashSet<string>(StringComparer.Ordinal) { TestPrincipal })
            ],
            [
                new TenantWrightEndpointRoute(
                    "tenantwright-phase2-unroutable",
                    TestPrincipal,
                    [typeof(NotificationSendRequestedTenantEto)])
            ],
            []);
        return BuildServiceProvider(CreateConfiguration(broker, keys), currentTenant, catalog);
    }

    private static IConfiguration CreateConfiguration(
        DockerRabbitMq broker,
        MessagingTestKeyMaterial keys) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{MessagingOptions.SectionName}:Provider"] = "MassTransit",
                [$"{MessagingOptions.SectionName}:ServiceName"] = TestPrincipal,
                [$"{MessagingOptions.SectionName}:ProducerPrincipal"] = TestPrincipal,
                [$"{MessagingOptions.SectionName}:RabbitMq:HostName"] = "127.0.0.1",
                [$"{MessagingOptions.SectionName}:RabbitMq:Port"] = broker.AmqpPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                [$"{MessagingOptions.SectionName}:RabbitMq:VirtualHost"] = broker.VirtualHost,
                [$"{MessagingOptions.SectionName}:RabbitMq:UserName"] = TestPrincipal,
                [$"{MessagingOptions.SectionName}:RabbitMq:Password"] = broker.Password,
                [$"{MessagingOptions.SectionName}:RabbitMq:UseTls"] = "false",
                [$"{MessagingOptions.SectionName}:RabbitMq:AllowInsecureTransport"] = "true",
                [$"{MessagingOptions.SectionName}:RabbitMq:PrefetchCount"] = "8",
                [$"{MessagingOptions.SectionName}:RabbitMq:ConcurrentMessageLimit"] = "4",
                [$"{MessagingOptions.SectionName}:Trust:ActiveKeyId"] = "phase2-test-key",
                [$"{MessagingOptions.SectionName}:Trust:PrivateKeyPath"] = keys.PrivateKeyPath,
                [$"{MessagingOptions.SectionName}:Trust:PublicKeys:0:KeyId"] = "phase2-test-key",
                [$"{MessagingOptions.SectionName}:Trust:PublicKeys:0:Actor"] = TestPrincipal,
                [$"{MessagingOptions.SectionName}:Trust:PublicKeys:0:PublicKeyPath"] = keys.PublicKeyPath
            })
            .Build();

    private static ServiceProvider BuildServiceProvider(
        IConfiguration configuration,
        MessagingTestCurrentTenant currentTenant,
        TenantWrightMessageCatalog catalog,
        RabbitMqConsumerState? consumerState = null,
        Action<IBusRegistrationContext, IRabbitMqReceiveEndpointConfigurator>? configureReceiveEndpoint = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHealthChecks();
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(
            new MessagingTestHostEnvironment("Testing"));
        services.AddSingleton(currentTenant);
        services.AddSingleton<ICurrentTenant>(currentTenant);
        if (consumerState is not null)
        {
            services.AddSingleton(consumerState);
            services.AddScoped<ITenantWrightMessageHandler<PermissionGrantsChangedTenantEto>, RabbitMqTrackedHandler>();
        }
        services.AddSingleton<IProtectedTerminalStore, MissingTerminalStore>();
        services.AddSingleton<IProtectedReplayAuthorization, RejectingReplayAuthorization>();
        services.AddSingleton<IProtectedReplayAudit, NoOpReplayAudit>();
        services.AddSingleton<IProtectedReplayIdempotencyStore, InMemoryReplayIdStore>();
        services.AddSingleton<ITenantWrightInboxTransaction>(provider => new InMemoryInboxTransaction(provider));
        services.AddSingleton<IProtectedReplayTransport, RejectingReplayTransport>();
        services.AddTenantWrightMessagingCore(configuration, catalog);
        services.AddTenantWrightMassTransit(catalog, configureReceiveEndpoint);
        return services.BuildServiceProvider(validateScopes: true);
    }

    /// <summary>
    /// Dispatches exactly as the outbox dispatcher does: the neutral stamper signs the origin under the ambient tenant,
    /// then the production MassTransit transport sends it and awaits broker confirmation.
    /// </summary>
    private sealed class StampingTransportDispatcher(IServiceProvider services)
    {
        public Task SendCommandAsync<T>(T command, CancellationToken cancellationToken) where T : class =>
            DispatchAsync(command, TenantWrightMessageRouteKind.Command, cancellationToken);

        public Task PublishEventAsync<T>(T message, CancellationToken cancellationToken) where T : class =>
            DispatchAsync(message, TenantWrightMessageRouteKind.Event, cancellationToken);

        private Task DispatchAsync(object message, TenantWrightMessageRouteKind kind, CancellationToken cancellationToken)
        {
            var stamped = services.GetRequiredService<TenantWrightOutboundMessageStamper>().Stamp(message, kind);
            return services.GetRequiredService<ITenantWrightMessagingTransport>().DispatchAsync(
                new TenantWrightTransportMessage(
                    stamped.MessageId,
                    stamped.ContractIdentity,
                    stamped.Kind,
                    stamped.Destination,
                    stamped.Payload,
                    stamped.Headers),
                cancellationToken);
        }
    }

    private sealed class LicensedRabbitMqFactAttribute : FactAttribute
    {
        public LicensedRabbitMqFactAttribute()
        {
            if (!string.Equals(
                    Environment.GetEnvironmentVariable("TENANTWRIGHT_RUN_MASSTRANSIT_RABBITMQ_TESTS"),
                    "1",
                    StringComparison.Ordinal))
            {
                Skip = "Requires TENANTWRIGHT_RUN_MASSTRANSIT_RABBITMQ_TESTS=1, Docker, and exactly one approved MT_LICENSE or MT_LICENSE_PATH interface.";
            }
        }
    }

    private sealed class RabbitMqDeliveryTracker(int expectedMessages)
    {
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _faulted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private string? _lastFailureCode;
        private int _handledCount;

        public int ExpectedMessages { get; } = expectedMessages;
        public int HandledCount => Volatile.Read(ref _handledCount);
        public ConcurrentDictionary<Guid, int> DeliveryCounts { get; } = new();
        public ConcurrentDictionary<string, int> InstanceCounts { get; } = new(StringComparer.Ordinal);
        public Task Completion => _completion.Task;
        public Task Faulted => _faulted.Task;
        public string? LastFailureCode => Volatile.Read(ref _lastFailureCode);

        public void RecordFailure(Type messageType, Exception exception) =>
            RecordFailure(messageType.FullName ?? messageType.Name, exception);

        public void RecordFailure(string operation, Exception exception)
        {
            var exceptionTypes = new List<string>(capacity: 5)
            {
                operation
            };
            for (var current = exception; current is not null && exceptionTypes.Count < 5; current = current.InnerException)
            {
                exceptionTypes.Add(current is MessagingSecurityException securityException
                    ? SecurityFailureCode(securityException)
                    : current.GetType().FullName ?? current.GetType().Name);
            }
            Interlocked.CompareExchange(ref _lastFailureCode, string.Join(" -> ", exceptionTypes), null);
            _faulted.TrySetResult();
        }

        private static string SecurityFailureCode(MessagingSecurityException exception)
        {
            var source = new StackTrace(exception, false).GetFrames()?
                .Select(frame => frame.GetMethod())
                .FirstOrDefault(method => method?.DeclaringType?.Namespace?.StartsWith("Shared.EventBus.Messaging", StringComparison.Ordinal) == true);
            return source is null
                ? exception.Message
                : $"{exception.Message} ({source.DeclaringType!.Name}.{source.Name})";
        }

        public void Record(Guid messageId, string instance)
        {
            var handledCount = Interlocked.Increment(ref _handledCount);
            DeliveryCounts.AddOrUpdate(messageId, 1, (_, count) => count + 1);
            InstanceCounts.AddOrUpdate(instance, 1, (_, count) => count + 1);
            if (handledCount >= ExpectedMessages)
                _completion.TrySetResult();
        }
    }

    private sealed class RabbitMqSendPropertyObserver : ISendObserver
    {
        private readonly TaskCompletionSource _faulted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Observed { get; private set; }
        public bool Mandatory { get; private set; }
        public bool AwaitAck { get; private set; }
        public bool PublishPayload { get; private set; }
        public bool TrustHeader { get; private set; }
        public string Diagnostic { get; private set; } = "not-observed";
        public Task Faulted => _faulted.Task;

        public Task PreSend<T>(SendContext<T> context) where T : class
        {
            Capture(context);
            return Task.CompletedTask;
        }

        public Task PostSend<T>(SendContext<T> context) where T : class
        {
            Capture(context);
            return Task.CompletedTask;
        }

        public Task SendFault<T>(SendContext<T> context, Exception exception) where T : class
        {
            Capture(context);
            _faulted.TrySetResult();
            return Task.CompletedTask;
        }

        private void Capture<T>(SendContext<T> context) where T : class
        {
            PublishPayload = context.TryGetPayload<PublishContext>(out _);
            TrustHeader = context.Headers.TryGetHeader(TenantWrightTrustHeaders.ContextVersion, out var value) && value is not null;
            if (context.TryGetPayload<RabbitMqSendContext>(out var rabbit))
            {
                Observed = true;
                Mandatory = rabbit.Mandatory;
                AwaitAck = rabbit.AwaitAck;
            }
            Diagnostic = string.Join(",",
                context.Message.GetType().FullName ?? context.Message.GetType().Name,
                $"publishMarker={context.TryGetPayload<TenantWrightPublishSendMarker>(out _)}",
                $"relayMarker={context.TryGetPayload<TenantWrightRelaySendMarker>(out _)}",
                $"redelivery={context.TryGetPayload<MessageRedeliveryContext>(out _)}",
                $"consume={context.TryGetPayload<ConsumeContext>(out _)}",
                $"mandatory={Mandatory}",
                $"trust={TrustHeader}");
        }
    }

    private sealed record RabbitMqConsumerState(
        RabbitMqDeliveryTracker Tracker,
        MessagingTestCurrentTenant CurrentTenant,
        string Instance,
        Func<PermissionGrantsChangedTenantEto, Task>? Handler);

    /// <summary>The test handler the production <see cref="TenantWrightMessageConsumer{TMessage}"/> forwards to.</summary>
    private sealed class RabbitMqTrackedHandler(RabbitMqConsumerState state)
        : ITenantWrightMessageHandler<PermissionGrantsChangedTenantEto>
    {
        public Task HandleAsync(PermissionGrantsChangedTenantEto message, CancellationToken cancellationToken)
        {
            // The consume side restores the tenant from the trusted header, so the message tenant and the ambient
            // tenant must agree here. For a host-only contract there would be no tenant to restore at all.
            Assert.Equal(message.TenantId, state.CurrentTenant.Id);
            if (state.Handler is not null)
                return state.Handler(message);

            state.Tracker.Record(MessageIdentityOf(message), state.Instance);
            return Task.CompletedTask;
        }
    }

    private sealed record HostOnlyConsumerState(MessagingTestCurrentTenant CurrentTenant)
    {
        private readonly TaskCompletionSource<Guid?> _observedTenant =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The ambient tenant observed inside the consumer, which must be absent for a host-only event.</summary>
        public Task<Guid?> ObservedTenant => _observedTenant.Task;

        public void Observe(Guid? tenantId) => _observedTenant.TrySetResult(tenantId);
    }

    private sealed class HostOnlyTrackedHandler(HostOnlyConsumerState state) : ITenantWrightMessageHandler<TenantDeactivatedEto>
    {
        public Task HandleAsync(TenantDeactivatedEto message, CancellationToken cancellationToken)
        {
            state.Observe(state.CurrentTenant.Id);
            return Task.CompletedTask;
        }
    }

    private sealed class RabbitMqErrorObserver(RabbitMqDeliveryTracker tracker) : IFilter<ExceptionReceiveContext>
    {
        public async Task Send(ExceptionReceiveContext context, IPipe<ExceptionReceiveContext> next)
        {
            tracker.RecordFailure("error-transport", context.Exception);
            await next.Send(context);
        }

        public void Probe(ProbeContext context) => context.CreateFilterScope("rabbitMqErrorObserver");
    }

    private sealed class RabbitMqConsumeFailureObserver(RabbitMqDeliveryTracker tracker) : IConsumeObserver
    {
        public Task PreConsume<T>(ConsumeContext<T> context) where T : class => Task.CompletedTask;

        public Task PostConsume<T>(ConsumeContext<T> context) where T : class => Task.CompletedTask;

        public Task ConsumeFault<T>(ConsumeContext<T> context, Exception exception) where T : class
        {
            tracker.RecordFailure(typeof(T), exception);
            return Task.CompletedTask;
        }
    }

    private sealed class MissingTerminalStore : IProtectedTerminalStore
    {
        public Task<ProtectedReplayMaterial?> FindAsync(string terminalId, CancellationToken cancellationToken) =>
            Task.FromResult<ProtectedReplayMaterial?>(null);
    }

    private sealed class RejectingReplayAuthorization : IProtectedReplayAuthorization
    {
        public Task AuthorizeAsync(
            ProtectedReplayRequest request,
            ProtectedTerminalSummary terminal,
            CancellationToken cancellationToken) =>
            Task.FromException(new MessagingSecurityException(MessagingSecurityException.ReplayRejected));
    }

    private sealed class NoOpReplayAudit : IProtectedReplayAudit
    {
        public Task RecordAsync(
            ProtectedReplayRequest request,
            ProtectedTerminalSummary terminal,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class InMemoryReplayIdStore : IProtectedReplayIdempotencyStore
    {
        private readonly ConcurrentDictionary<string, ProtectedReplayIssue> _issued = new(StringComparer.Ordinal);

        public Task<bool> TryIssueAsync(ProtectedReplayIssue issue, CancellationToken cancellationToken) =>
            Task.FromResult(_issued.TryAdd(issue.ReplayId, issue));

        public Task<bool> TryConsumeIssuedAsync(
            ProtectedReplayConsumption consumption,
            CancellationToken cancellationToken)
        {
            if (!_issued.TryGetValue(consumption.ReplayId, out var issue) ||
                !string.Equals(issue.Destination, consumption.Destination, StringComparison.Ordinal) ||
                !string.Equals(issue.AuditId, consumption.AuditId, StringComparison.Ordinal) ||
                issue.ExpiresAt != consumption.ExpiresAt ||
                consumption.ConsumedAt >= issue.ExpiresAt)
                return Task.FromResult(false);
            return Task.FromResult(_issued.TryRemove(consumption.ReplayId, out _));
        }
    }

    private sealed class RejectingReplayTransport : IProtectedReplayTransport
    {
        public Task SendAsync(ProtectedReplayDispatch dispatch, CancellationToken cancellationToken) =>
            Task.FromException(new MessagingSecurityException(MessagingSecurityException.ReplayRejected));
    }

    private sealed class DockerRabbitMq : IAsyncDisposable
    {
        private const string Image = "rabbitmq:3.13-management";
        private readonly string _containerName;

        private DockerRabbitMq(
            string containerName,
            string virtualHost,
            string password,
            int amqpPort,
            int managementPort)
        {
            _containerName = containerName;
            VirtualHost = virtualHost;
            Password = password;
            AmqpPort = amqpPort;
            ManagementPort = managementPort;
        }

        public string VirtualHost { get; }
        public string Password { get; }
        public int AmqpPort { get; }
        public int ManagementPort { get; }

        public static async Task<DockerRabbitMq> StartAsync()
        {
            var suffix = Guid.NewGuid().ToString("N");
            var name = $"tenantwright-mt-phase2-{suffix}";
            var vhost = $"tenantwright-phase2-{suffix}";
            var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            var amqpPort = AvailableLoopbackPort();
            var managementPort = AvailableLoopbackPort();
            while (managementPort == amqpPort)
                managementPort = AvailableLoopbackPort();
            var run = await RunDockerAsync(
                [
                    "run", "--detach", "--name", name, "--hostname", name,
                    "--publish", $"127.0.0.1:{amqpPort}:5672", "--publish", $"127.0.0.1:{managementPort}:15672",
                    "--env", $"RABBITMQ_DEFAULT_USER={TestPrincipal}",
                    "--env", $"RABBITMQ_DEFAULT_PASS={password}",
                    "--env", $"RABBITMQ_DEFAULT_VHOST={vhost}",
                    // check_running rather than ping, and a 3s interval with a start period rather than 1s. Every
                    // rabbitmq-diagnostics invocation starts an Erlang CLI node that reads and writes
                    // /var/lib/rabbitmq/.erlang.cookie; at a 1s interval those raced the broker's own cookie
                    // initialization during its ~18s boot and the broker died in prelaunch with an eacces on that file.
                    // The probe was breaking the thing it measured. Same fix already proved out in the
                    // Shared.EntityFrameworkCore fixture. The overall two-minute readiness bound is unchanged.
                    "--health-cmd", "rabbitmq-diagnostics -q check_running",
                    "--health-start-period", "20s",
                    "--health-interval", "3s", "--health-timeout", "10s", "--health-retries", "40",
                    Image
                ],
                TimeSpan.FromMinutes(2));
            if (run.ExitCode != 0)
                throw new InvalidOperationException("The isolated RabbitMQ test container could not be started.");

            try
            {
                await WaitForHealthyAsync(name);
                if (await PublishedPortAsync(name, "5672/tcp") != amqpPort ||
                    await PublishedPortAsync(name, "15672/tcp") != managementPort)
                    throw new InvalidOperationException("The isolated RabbitMQ test ports did not match the explicit stable bindings.");
                return new DockerRabbitMq(name, vhost, password, amqpPort, managementPort);
            }
            catch
            {
                await RemoveAsync(name);
                throw;
            }
        }

        public async Task<bool> IsQuorumQueueAsync(string queueName, CancellationToken cancellationToken)
        {
            using var client = CreateManagementClient();
            using var document = await GetQueueAsync(client, queueName, cancellationToken);
            return document is not null &&
                   document.RootElement.TryGetProperty("type", out var type) &&
                   string.Equals(type.GetString(), "quorum", StringComparison.Ordinal);
        }

        public async Task<QueueState> GetQueueStateAsync(string queueName, CancellationToken cancellationToken)
        {
            using var client = CreateManagementClient();
            using var queue = await GetQueueAsync(client, queueName, cancellationToken);
            if (queue is null)
                return new QueueState(false, 0, 0);

            return new QueueState(
                true,
                queue.RootElement.GetProperty("messages").GetInt32(),
                queue.RootElement.GetProperty("consumers").GetInt32());
        }

        public async Task<TerminalMessage> WaitForTerminalMessageAsync(
            string queueName,
            CancellationToken cancellationToken)
        {
            using var client = CreateManagementClient();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var queue = await GetQueueAsync(client, queueName, cancellationToken);
                if (queue is not null &&
                    queue.RootElement.TryGetProperty("messages", out var messageCount) &&
                    messageCount.GetInt32() > 0)
                {
                    var queueType = queue.RootElement.GetProperty("type").GetString() ?? string.Empty;
                    var path = $"/api/queues/{Uri.EscapeDataString(VirtualHost)}/{Uri.EscapeDataString(queueName)}/get";
                    using var body = new StringContent(
                        "{\"count\":1,\"ackmode\":\"ack_requeue_true\",\"encoding\":\"auto\",\"truncate\":50000}",
                        Encoding.UTF8,
                        "application/json");
                    using var response = await client.PostAsync(path, body, cancellationToken);
                    response.EnsureSuccessStatusCode();
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    using var messages = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                    var message = messages.RootElement.EnumerateArray().Single();
                    var properties = message.GetProperty("properties");
                    var userId = properties.GetProperty("user_id").GetString() ?? string.Empty;
                    var headersElement = properties.GetProperty("headers");
                    var headers = headersElement.EnumerateObject()
                        .Select(property => property.Name)
                        .ToHashSet(StringComparer.Ordinal);
                    var faultExceptionType = headersElement.TryGetProperty("MT-Fault-ExceptionType", out var faultType)
                        ? faultType.GetString() ?? "missing"
                        : "missing";
                    return new TerminalMessage(queueType, userId, headers, faultExceptionType);
                }
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }

        public async Task KillContainerAsync()
        {
            var result = await RunDockerAsync(["kill", "--signal=KILL", _containerName], TimeSpan.FromSeconds(30));
            if (result.ExitCode != 0)
                throw new InvalidOperationException("The isolated RabbitMQ test container could not be killed for the node-loss simulation.");
        }

        public async Task StartContainerAsync()
        {
            var result = await RunDockerAsync(["start", _containerName], TimeSpan.FromSeconds(30));
            if (result.ExitCode != 0)
                throw new InvalidOperationException("The isolated RabbitMQ test container could not be restarted.");
            await WaitForHealthyAsync(_containerName);
            if (await PublishedPortAsync(_containerName, "5672/tcp") != AmqpPort ||
                await PublishedPortAsync(_containerName, "15672/tcp") != ManagementPort)
                throw new InvalidOperationException("The isolated RabbitMQ test ports changed across restart.");
        }

        public async ValueTask DisposeAsync() => await RemoveAsync(_containerName);

        private HttpClient CreateManagementClient()
        {
            var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{ManagementPort}") };
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{TestPrincipal}:{Password}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
            return client;
        }

        private async Task<JsonDocument?> GetQueueAsync(
            HttpClient client,
            string queueName,
            CancellationToken cancellationToken)
        {
            var path = $"/api/queues/{Uri.EscapeDataString(VirtualHost)}/{Uri.EscapeDataString(queueName)}";
            using var response = await client.GetAsync(path, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }

        /// <summary>
        /// Waits for the container's own health check, reporting what it observed when it gives up.
        /// </summary>
        /// <remarks>
        /// The wait is bounded by a stopwatch rather than a cancellation token. Cancelling the inner
        /// <c>Task.Delay</c> made the terminal <see cref="TimeoutException"/> unreachable: the wait always surfaced as
        /// an opaque <c>TaskCanceledException</c> from the delay, so a genuine readiness failure was reported without
        /// the container state, health status, or probe output needed to diagnose it.
        ///
        /// The bound itself is unchanged. Only the reporting is.
        /// </remarks>
        private static async Task WaitForHealthyAsync(string name)
        {
            var deadline = TimeSpan.FromMinutes(2);
            var stopwatch = Stopwatch.StartNew();
            var lastStatus = "not-observed";
            while (stopwatch.Elapsed < deadline)
            {
                var inspect = await RunDockerAsync(
                    ["inspect", "--format={{.State.Status}}|{{.State.Health.Status}}|{{.State.Health.FailingStreak}}", name],
                    TimeSpan.FromSeconds(10));
                lastStatus = inspect.ExitCode == 0 ? inspect.StandardOutput.Trim() : $"inspect-exit={inspect.ExitCode}";
                if (lastStatus.Contains("|healthy|", StringComparison.Ordinal))
                    return;
                await Task.Delay(TimeSpan.FromSeconds(1));
            }

            // The probe output is RabbitMQ diagnostics text, not credentials; the container password is never echoed.
            var probe = await RunDockerAsync(
                ["inspect", "--format={{range .State.Health.Log}}exit={{.ExitCode}} out={{.Output}}{{end}}", name],
                TimeSpan.FromSeconds(10));
            throw new TimeoutException(
                $"The isolated RabbitMQ test container did not become healthy within {deadline}. " +
                $"Last observed state: {lastStatus}. Health probe log: {probe.StandardOutput.Trim()}");
        }

        private static async Task<int> PublishedPortAsync(string name, string containerPort)
        {
            var result = await RunDockerAsync(["port", name, containerPort], TimeSpan.FromSeconds(10));
            if (result.ExitCode != 0)
                throw new InvalidOperationException("The isolated RabbitMQ test port could not be resolved.");
            var value = result.StandardOutput.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
            var separator = value.LastIndexOf(':');
            if (separator < 0 || !int.TryParse(value[(separator + 1)..], out var port))
                throw new InvalidOperationException("The isolated RabbitMQ test port could not be resolved.");
            return port;
        }

        private static int AvailableLoopbackPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                listener.Stop();
            }
        }

        private static async Task RemoveAsync(string name)
        {
            // --volumes matters: the RabbitMQ image declares VOLUME /var/lib/rabbitmq, so every `docker run` creates an
            // anonymous volume. Removing the container without it left one behind per container per test run. This was the
            // remaining leak after the Shared.EntityFrameworkCore fixture was corrected.
            _ = await RunDockerAsync(["rm", "--force", "--volumes", name], TimeSpan.FromSeconds(30));
        }

        private static async Task<DockerResult> RunDockerAsync(
            IReadOnlyList<string> arguments,
            TimeSpan timeout)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "docker",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            foreach (var argument in arguments)
                process.StartInfo.ArgumentList.Add(argument);
            if (!process.Start())
                throw new InvalidOperationException("Docker could not be started for the isolated RabbitMQ test.");
            using var cancellation = new CancellationTokenSource(timeout);
            var standardOutput = process.StandardOutput.ReadToEndAsync(cancellation.Token);
            var standardError = process.StandardError.ReadToEndAsync(cancellation.Token);
            await process.WaitForExitAsync(cancellation.Token);
            await standardError;
            return new DockerResult(process.ExitCode, await standardOutput);
        }

        public sealed record QueueState(bool Exists, int Messages, int Consumers);

        public sealed record TerminalMessage(
            string QueueType,
            string UserId,
            IReadOnlySet<string> HeaderNames,
            string FaultExceptionType);

        private sealed record DockerResult(int ExitCode, string StandardOutput);
    }
}
