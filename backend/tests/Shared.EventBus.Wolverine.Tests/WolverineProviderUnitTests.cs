using System.Text;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using Shared.EventBus.Messaging;
using Shared.EventContracts;
using Shared.MultiTenancy;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace Shared.EventBus.Wolverine.Tests;

/// <summary>Broker-free proofs of the Wolverine provider's registration, envelope mapping and outbound properties.</summary>
public sealed class WolverineProviderUnitTests : IDisposable
{
    private static readonly WolverineBrokerTarget UnusedBroker = new("127.0.0.1", 5672, "tenantwright-wolverine-unit", "unused");
    private readonly MessagingTestKeys _keys = new([.. WolverineTestTopology.Principals]);

    public void Dispose() => _keys.Dispose();

    [Fact]
    public async Task Provider_registers_replay_custody_but_no_delayed_redelivery_verifier()
    {
        await using var provider = BuildProvider(WolverineTestTopology.ConsumerCatalog(), WolverineTestTopology.Consumer);

        Assert.IsType<WolverineMessagingTransport>(provider.GetRequiredService<ITenantWrightMessagingTransport>());
        Assert.Contains(provider.GetServices<IHostedService>(), service => service is IWolverineRuntime);
        Assert.Empty(provider.GetServices<ITenantWrightRedeliveryCustodyVerifier>());
        Assert.IsType<WolverineReplayCustodyVerifier>(Assert.Single(provider.GetServices<ITenantWrightReplayCustodyVerifier>()));
        Assert.IsType<WolverineProtectedReplayTransport>(provider.GetRequiredService<IProtectedReplayTransport>());
        Assert.IsType<WolverineTerminalMessageReader>(provider.GetRequiredService<WolverineTerminalMessageReader>());
        Assert.IsType<WolverineDiscoveryLogger>(provider.GetRequiredService<ILogger<HandlerDiscovery>>());

        var health = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;
        var readiness = Assert.Single(health, registration => registration.Name == WolverineMessagingHealthCheck.Name);
        Assert.Equal(["ready"], readiness.Tags);
        Assert.Equal(HealthStatus.Unhealthy, readiness.FailureStatus);
    }

    [Fact]
    public async Task Production_autofac_container_resolves_the_provider_registrations()
    {
        var configuration = WolverineTestConfiguration.Build(WolverineTestConfiguration.Messaging(
            UnusedBroker, _keys, WolverineTestTopology.Consumer, WolverineTestTopology.Principals));
        var catalog = WolverineTestTopology.ConsumerCatalog();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(Environments.Development));
        services.AddSingleton<ICurrentTenant, TestAmbientTenant>();
        services.AddTenantWrightMessagingCore(configuration, catalog);
        new WolverineMessagingProvider().Register(services, configuration, catalog);

        // The provider factory every service host installs through UseSharedAutofac.
        var factory = new Shared.Autofac.AutofacServiceProviderFactory();
        var provider = factory.CreateServiceProvider(factory.CreateBuilder(services));
        await using var disposal = (IAsyncDisposable)provider;

        // Autofac must honour the closed discovery-logger registration over the open generic ILogger<>.
        Assert.IsType<WolverineDiscoveryLogger>(provider.GetRequiredService<ILogger<HandlerDiscovery>>());
        Assert.IsType<WolverineMessagingTransport>(provider.GetRequiredService<ITenantWrightMessagingTransport>());
        Assert.IsType<TenantWrightWolverineMessageHandler<PermissionGrantsChangedTenantEto>>(
            CompiledHandlerGraph(provider).HandlerFor<PermissionGrantsChangedTenantEto>());
    }

    [Fact]
    public async Task Wolverine_is_confined_to_inline_quorum_consumption_without_persistence_or_discovery()
    {
        await using var provider = BuildProvider(WolverineTestTopology.ConsumerCatalog(), WolverineTestTopology.Consumer);
        var options = provider.GetRequiredService<WolverineOptions>();

        Assert.Equal(DurabilityMode.Solo, options.Durability.Mode);
        Assert.False(options.Durability.DurabilityAgentEnabled);
        Assert.Equal(TypeLoadMode.Static, options.CodeGeneration.TypeLoadMode);
        Assert.Equal(UnknownMessageBehavior.DeadLetterQueue, options.UnknownMessageBehavior);
        Assert.False(options.EnableAutomaticFailureAcks);
        Assert.Equal(WolverineTestTopology.Consumer, options.ServiceName);
        Assert.Equal(typeof(WolverineMessagingProvider).Assembly, options.ApplicationAssembly);
    }

    [Fact]
    public async Task Every_consumed_contract_gets_one_prebuilt_handler_resolvable_by_its_contract_identity()
    {
        await using var provider = BuildProvider(WolverineTestTopology.ConsumerCatalog(), WolverineTestTopology.Consumer);
        var handlers = CompiledHandlerGraph(provider);

        Assert.IsType<TenantWrightWolverineMessageHandler<NotificationSendRequestedTenantEto>>(
            handlers.HandlerFor<NotificationSendRequestedTenantEto>());
        Assert.IsType<TenantWrightWolverineMessageHandler<PermissionGrantsChangedTenantEto>>(
            handlers.HandlerFor<PermissionGrantsChangedTenantEto>());
        Assert.Null(handlers.HandlerFor<NotificationSendRequestedHostEto>());

        Assert.True(handlers.TryFindMessageType(WolverineTestTopology.GrantsExchange, out var grants));
        Assert.Equal(typeof(PermissionGrantsChangedTenantEto), grants);

        // Nothing is code-generated: every chain Wolverine would compile at runtime is absent.
        Assert.DoesNotContain(handlers.AllChains(), chain => chain.Handlers.Count > 0);
    }

    [Fact]
    public async Task A_handler_shaped_class_in_a_scanned_assembly_is_not_registered()
    {
        // Control: with Wolverine's defaults this assembly's FooHandler is a discoverable handler.
        var control = new ServiceCollection();
        control.AddLogging();
        control.AddWolverine(ExtensionDiscovery.ManualOnly, options =>
        {
            options.ApplicationAssembly = typeof(FooHandler).Assembly;
            options.CodeGeneration.TypeLoadMode = TypeLoadMode.Static;
        });
        await using (var controlProvider = control.BuildServiceProvider())
        {
            Assert.True(CompiledHandlerGraph(controlProvider).CanHandle(typeof(FooMessage)));
        }

        // The provider, with this assembly explicitly added to the scanned set.
        await using var provider = BuildProvider(
            WolverineTestTopology.ConsumerCatalog(),
            WolverineTestTopology.Consumer,
            services => services.AddWolverineExtension<ScanTestAssembly>());
        var handlers = CompiledHandlerGraph(provider);

        Assert.Contains(typeof(FooHandler).Assembly, provider.GetRequiredService<WolverineOptions>().Assemblies);
        Assert.False(handlers.CanHandle(typeof(FooMessage)));
        Assert.DoesNotContain(handlers.AllChains(), chain => chain.MessageType == typeof(FooMessage));
    }

    [Fact]
    public void Mapper_carries_the_broker_facts_and_only_the_reserved_trust_headers()
    {
        var catalog = WolverineTestTopology.ConsumerCatalog();
        var endpoint = catalog.GetReceiveEndpoint(WolverineTestTopology.CommandEndpoint);
        var mapper = new TenantWrightRabbitMqEnvelopeMapper(endpoint, catalog);
        var messageId = Guid.NewGuid();
        var envelope = new Envelope();

        mapper.MapIncomingToEnvelope(envelope, new BasicProperties
        {
            MessageId = messageId.ToString("D"),
            UserId = WolverineTestTopology.Producer,
            Type = TenantWrightMessageContracts.GetIdentity(typeof(NotificationSendRequestedTenantEto)),
            ContentType = "application/json",
            Headers = new Dictionary<string, object?>
            {
                [TenantWrightTrustHeaders.Producer] = Encoding.UTF8.GetBytes(WolverineTestTopology.Producer),
                [TenantWrightTrustHeaders.ContextVersion] = Encoding.UTF8.GetBytes("1"),
                [TenantWrightTrustHeaders.OriginSignature] = new byte[] { 0xC3, 0x28 },
                [TenantWrightTrustHeaders.TenantId] = 42,
                ["reply-uri"] = Encoding.UTF8.GetBytes("rabbitmq://queue/elsewhere"),
                ["x-tenantwright-unknown"] = Encoding.UTF8.GetBytes("ignored")
            }
        });

        var delivery = Assert.IsType<TenantWrightRabbitMqDelivery>(envelope.RoutingInformation);
        Assert.Equal(WolverineTestTopology.CommandEndpoint, delivery.Endpoint);
        Assert.Equal(messageId, delivery.MessageId);
        Assert.Equal(messageId, envelope.Id);
        Assert.Equal(WolverineTestTopology.Producer, delivery.BrokerActor);
        Assert.False(Assert.IsType<WolverineRabbitMqTransportEvidence>(delivery.TransportEvidence).HasRedeliveryHistory);
        Assert.Equal(typeof(NotificationSendRequestedTenantEto).FullName, envelope.MessageType);
        Assert.Equal("application/json", envelope.ContentType);
        Assert.IsType<TenantWrightCanonicalMessageSerializer>(envelope.Serializer);

        string[] expectedHeaders =
        [
            TenantWrightTrustHeaders.ContextVersion, TenantWrightTrustHeaders.Producer,
            TenantWrightTrustHeaders.TenantId, TenantWrightTrustHeaders.OriginSignature
        ];
        Assert.Equal(
            expectedHeaders.Order(StringComparer.Ordinal),
            delivery.TrustHeaders.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(WolverineTestTopology.Producer, delivery.TrustHeaders[TenantWrightTrustHeaders.Producer]);

        // Present but unreadable stays present (custody evidence) with no value.
        Assert.Null(delivery.TrustHeaders[TenantWrightTrustHeaders.OriginSignature]);
        Assert.Null(delivery.TrustHeaders[TenantWrightTrustHeaders.TenantId]);
        Assert.Null(envelope.ReplyUri);
    }

    [Theory]
    [InlineData("Shared.EventContracts.PermissionGrantsChangedTenantEto")]
    [InlineData("Wolverine.Runtime.Agents.StartAgent")]
    [InlineData(null)]
    public void Mapper_refuses_any_message_type_outside_the_endpoint(string? wireType)
    {
        var catalog = WolverineTestTopology.ConsumerCatalog();
        var mapper = new TenantWrightRabbitMqEnvelopeMapper(catalog.GetReceiveEndpoint(WolverineTestTopology.CommandEndpoint), catalog);
        var envelope = new Envelope();

        mapper.MapIncomingToEnvelope(envelope, new BasicProperties { Type = wireType, MessageId = "not-a-guid" });

        Assert.Null(envelope.MessageType);
        var delivery = Assert.IsType<TenantWrightRabbitMqDelivery>(envelope.RoutingInformation);
        Assert.Null(delivery.MessageId);
        Assert.NotEqual(Guid.Empty, envelope.Id);
        Assert.Null(delivery.BrokerActor);
        Assert.Empty(delivery.TrustHeaders);
    }

    [Fact]
    public void Mapper_reports_dead_letter_history_so_the_verifier_rejects_it()
    {
        var catalog = WolverineTestTopology.ConsumerCatalog();
        var mapper = new TenantWrightRabbitMqEnvelopeMapper(catalog.GetReceiveEndpoint(WolverineTestTopology.CommandEndpoint), catalog);
        var envelope = new Envelope();

        mapper.MapIncomingToEnvelope(envelope, new BasicProperties
        {
            Headers = new Dictionary<string, object?> { ["x-death"] = new List<object>() }
        });

        var delivery = Assert.IsType<TenantWrightRabbitMqDelivery>(envelope.RoutingInformation);
        Assert.True(delivery.TransportEvidence.HasRedeliveryHistory);
    }

    [Fact]
    public async Task Outbound_properties_round_trip_the_stamped_origin_through_the_wire_representation()
    {
        await using var provider = BuildProvider(WolverineTestTopology.ProducerCatalog(), WolverineTestTopology.Producer);
        var tenantId = Guid.NewGuid();
        var stamped = Stamp(provider, NewCommand(tenantId), tenantId);
        var transportMessage = new TenantWrightTransportMessage(
            stamped.MessageId, stamped.ContractIdentity, stamped.Kind, stamped.Destination, stamped.Payload, stamped.Headers);
        var now = DateTimeOffset.UtcNow;

        var properties = WolverineMessagingTransport.CreateProperties(transportMessage, stamped.Origin.OriginPrincipal, now);

        Assert.Equal(stamped.MessageId.ToString("D"), properties.MessageId);
        Assert.Equal(WolverineTestTopology.Producer, properties.UserId);
        Assert.Equal(stamped.ContractIdentity, properties.Type);
        Assert.Equal("application/json", properties.ContentType);
        Assert.Equal(DeliveryModes.Persistent, properties.DeliveryMode);
        Assert.Equal(now.ToUnixTimeSeconds(), properties.Timestamp.UnixTime);

        // RabbitMQ delivers string headers back as UTF-8 byte arrays.
        var wire = new BasicProperties
        {
            MessageId = properties.MessageId,
            UserId = properties.UserId,
            Type = properties.Type,
            ContentType = properties.ContentType,
            Headers = properties.Headers!.ToDictionary(
                header => header.Key, object? (header) => Encoding.UTF8.GetBytes((string)header.Value!))
        };
        var catalog = WolverineTestTopology.ConsumerCatalog();
        var envelope = new Envelope();
        new TenantWrightRabbitMqEnvelopeMapper(catalog.GetReceiveEndpoint(WolverineTestTopology.CommandEndpoint), catalog)
            .MapIncomingToEnvelope(envelope, wire);
        var delivery = Assert.IsType<TenantWrightRabbitMqDelivery>(envelope.RoutingInformation);

        Assert.Equal(stamped.Origin, TenantWrightTrustHeaderMap.ReadOrigin(delivery.TrustHeaders, delivery.MessageId!.Value, stamped.ContractIdentity));
    }

    [Fact]
    public void Outbound_properties_refuse_a_header_that_is_not_a_reserved_trust_header()
    {
        var message = new TenantWrightTransportMessage(
            Guid.NewGuid(), "contract", TenantWrightMessageRouteKind.Event, null, Array.Empty<byte>(),
            new Dictionary<string, string> { ["reply-uri"] = "rabbitmq://queue/elsewhere" });

        var exception = Assert.Throws<MessagingSecurityException>(() =>
            WolverineMessagingTransport.CreateProperties(message, WolverineTestTopology.Producer, DateTimeOffset.UtcNow));
        Assert.Equal(MessagingSecurityException.InvalidContext, exception.Message);
    }

    [Fact]
    public async Task Transport_refuses_a_tampered_payload_before_touching_the_broker()
    {
        await using var provider = BuildProvider(WolverineTestTopology.ProducerCatalog(), WolverineTestTopology.Producer);
        var tenantId = Guid.NewGuid();
        var stamped = Stamp(provider, NewCommand(tenantId), tenantId);
        var catalog = provider.GetRequiredService<TenantWrightMessageCatalog>();
        var tampered = NewCommand(tenantId);
        tampered.RequestId = Guid.NewGuid();
        var transport = provider.GetRequiredService<ITenantWrightMessagingTransport>();

        var exception = await Assert.ThrowsAsync<MessagingSecurityException>(() => transport.DispatchAsync(
            new TenantWrightTransportMessage(stamped.MessageId, stamped.ContractIdentity, stamped.Kind, stamped.Destination,
                catalog.SerializeCanonical(tampered, typeof(NotificationSendRequestedTenantEto)), stamped.Headers),
            CancellationToken.None));

        Assert.Equal(MessagingSecurityException.InvalidSignature, exception.Message);
    }

    [Fact]
    public async Task Transport_refuses_a_command_whose_destination_differs_from_the_catalog()
    {
        await using var provider = BuildProvider(WolverineTestTopology.ProducerCatalog(), WolverineTestTopology.Producer);
        var tenantId = Guid.NewGuid();
        var stamped = Stamp(provider, NewCommand(tenantId), tenantId);
        var transport = provider.GetRequiredService<ITenantWrightMessagingTransport>();

        var exception = await Assert.ThrowsAsync<MessagingSecurityException>(() => transport.DispatchAsync(
            new TenantWrightTransportMessage(stamped.MessageId, stamped.ContractIdentity, stamped.Kind,
                WolverineTestTopology.FirstEventEndpoint, stamped.Payload, stamped.Headers),
            CancellationToken.None));

        Assert.Equal(MessagingSecurityException.UntrustedCustody, exception.Message);
    }

    [Theory]
    [InlineData("text/plain", typeof(NotificationSendRequestedTenantEto), "{}")]
    [InlineData("application/json", typeof(PermissionGrantsChangedTenantEto), "{}")]
    [InlineData("application/json", typeof(NotificationSendRequestedTenantEto), "{not json")]
    public void Serializer_rejects_anything_but_canonical_json_of_an_endpoint_contract(
        string contentType,
        Type messageType,
        string body)
    {
        var catalog = WolverineTestTopology.ConsumerCatalog();
        var serializer = new TenantWrightCanonicalMessageSerializer(
            catalog, catalog.GetReceiveEndpoint(WolverineTestTopology.CommandEndpoint));
        var envelope = new Envelope { ContentType = contentType, Data = Encoding.UTF8.GetBytes(body) };

        Assert.Throws<MessagingSecurityException>(() => serializer.ReadFromData(messageType, envelope));
        Assert.Throws<NotSupportedException>(() => serializer.WriteMessage(new object()));
    }

    [Fact]
    public void Serializer_reproduces_the_canonical_payload()
    {
        var catalog = WolverineTestTopology.ConsumerCatalog();
        var serializer = new TenantWrightCanonicalMessageSerializer(
            catalog, catalog.GetReceiveEndpoint(WolverineTestTopology.CommandEndpoint));
        var command = NewCommand(Guid.NewGuid());
        var canonical = catalog.SerializeCanonical(command, typeof(NotificationSendRequestedTenantEto));

        var read = serializer.ReadFromData(
            typeof(NotificationSendRequestedTenantEto),
            new Envelope { ContentType = "application/json", Data = canonical });

        Assert.Equal(canonical, catalog.SerializeCanonical(read, typeof(NotificationSendRequestedTenantEto)));
    }

    internal static NotificationSendRequestedTenantEto NewCommand(Guid tenantId) => new()
    {
        TenantId = tenantId,
        RequestId = Guid.NewGuid(),
        DeliveryPlan = new NotificationDeliveryPlan
        {
            Steps = [new NotificationChannelPlan { Channel = NotificationSendChannel.Email, TemplateName = "wolverine-test" }]
        },
        SourceService = WolverineTestTopology.Producer,
        OccurredAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    private ServiceProvider BuildProvider(
        TenantWrightMessageCatalog catalog,
        string principal,
        Action<IServiceCollection>? configure = null)
    {
        var configuration = WolverineTestConfiguration.Build(
            WolverineTestConfiguration.Messaging(UnusedBroker, _keys, principal, WolverineTestTopology.Principals));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(Environments.Development));
        services.AddSingleton<ICurrentTenant, TestAmbientTenant>();
        services.AddTenantWrightMessagingCore(configuration, catalog);
        new WolverineMessagingProvider().Register(services, configuration, catalog);
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static TenantWrightOutboundMessage Stamp(IServiceProvider provider, object message, Guid tenantId)
    {
        using var tenant = provider.GetRequiredService<ICurrentTenant>().Change(tenantId);
        using var scope = provider.CreateScope();
        return scope.ServiceProvider.GetRequiredService<TenantWrightOutboundMessageStamper>()
            .Stamp(message, TenantWrightMessageRouteKind.Command);
    }

    /// <summary>Resolving the code file collection compiles the handler graph exactly as runtime start does.</summary>
    private static HandlerGraph CompiledHandlerGraph(IServiceProvider provider)
    {
        _ = provider.GetRequiredService<JasperFx.CodeGeneration.ICodeFileCollection>();
        return provider.GetRequiredService<HandlerGraph>();
    }
}

/// <summary>Adds this test assembly to the provider's discovery set, so discovery has a handler-shaped type to find.</summary>
internal sealed class ScanTestAssembly : IWolverineExtension
{
    public void Configure(WolverineOptions options) => options.Discovery.IncludeAssembly(typeof(FooHandler).Assembly);
}

/// <summary>A message shape Wolverine's conventions would treat as handled by <see cref="FooHandler"/>.</summary>
public sealed record FooMessage(Guid Id);

/// <summary>
/// Named and shaped exactly like the many <c>*Handler</c> classes in the services (<c>HandleAsync</c> taking a message),
/// which Wolverine's conventional discovery would wire up as message handlers.
/// </summary>
public sealed class FooHandler(ILogger<FooHandler> logger)
{
    public Task HandleAsync(FooMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        logger.LogInformation("FooHandler handled {MessageId}.", message.Id);
        return Task.CompletedTask;
    }
}
