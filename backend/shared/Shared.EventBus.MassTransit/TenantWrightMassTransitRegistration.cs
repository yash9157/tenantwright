using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Shared.EventBus.Messaging;
using Shared.EventContracts;

namespace Shared.EventBus.MassTransit;

internal static class TenantWrightMassTransitRegistration
{
    /// <summary>
    /// Registers MassTransit as the thin transport over the neutral messaging core for <paramref name="catalog"/>:
    /// <see cref="MassTransitMessagingTransport"/> for outbox dispatch, and one
    /// <see cref="TenantWrightMessageConsumer{TMessage}"/> per receive-endpoint contract that adapts each delivery into the
    /// neutral <see cref="TenantWrightInboundMessagePipeline"/>. MassTransit owns only the RabbitMQ-specific custody:
    /// queue-based delayed redelivery, terminal error/dead-letter custody and the protected replay send path.
    /// </summary>
    /// <remarks>
    /// The neutral core (<see cref="TenantWrightMessagingRegistration.AddTenantWrightMessagingCore"/>) must already be
    /// registered, and the host's persistence layer must register the durable <see cref="ITenantWrightInboxTransaction"/>
    /// and <see cref="IProtectedReplayIdempotencyStore"/>; bus configuration fails closed without them.
    /// </remarks>
    /// <param name="configureReceiveEndpoint">
    /// Additional middleware applied identically to every local receive endpoint after the redelivery and retry
    /// pipeline and before its consumers are attached.
    /// </param>
    public static IServiceCollection AddTenantWrightMassTransit(
        this IServiceCollection services,
        TenantWrightMessageCatalog catalog,
        Action<IBusRegistrationContext, IRabbitMqReceiveEndpointConfigurator>? configureReceiveEndpoint = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(catalog);

        services.AddSingleton<MassTransitLicenseResolver>();
        services.AddSingleton<ITenantWrightMessagingTransport, MassTransitMessagingTransport>();
        services.AddSingleton<ITenantWrightRedeliveryCustodyVerifier, MassTransitRedeliveryCustodyVerifier>();
        services.AddSingleton<ITenantWrightReplayCustodyVerifier, MassTransitReplayCustodyVerifier>();
        services.AddScoped(typeof(TenantWrightSendTrustFilter<>));
        services.AddScoped(typeof(TenantWrightPublishTrustFilter<>));
        services.AddScoped(typeof(TenantWrightRedeliveryTrustFilter<>));

        services.AddOptions<MassTransitHostOptions>().Configure<IOptions<MessagingOptions>>((host, messaging) =>
        {
            host.StopTimeout = messaging.Value.Delivery.StopTimeout;
            host.ConsumerStopTimeout = messaging.Value.Delivery.StopTimeout;
        });

        services.AddMassTransit(registration =>
        {
            registration.SetKebabCaseEndpointNameFormatter();
            foreach (var contractType in catalog.ReceiveEndpoints.SelectMany(endpoint => endpoint.ContractTypes).Distinct())
                registration.AddConsumer(ConsumerType(contractType));
            registration.UsingRabbitMq((context, bus) => ConfigureBus(context, bus, catalog, configureReceiveEndpoint));
        });

        services.Configure<global::MassTransit.Configuration.MassTransitHealthCheckOptions<IBus>>(health =>
        {
            health.Name = "tenantwright-masstransit";
            health.MinimalFailureStatus = HealthStatus.Unhealthy;
            health.Tags.Add("ready");
        });
        return services;
    }

    private static void ConfigureBus(
        IBusRegistrationContext context,
        IRabbitMqBusFactoryConfigurator bus,
        TenantWrightMessageCatalog catalog,
        Action<IBusRegistrationContext, IRabbitMqReceiveEndpointConfigurator>? configureReceiveEndpoint)
    {
        TenantWrightMessagingProviderPreconditions.Verify(context, catalog, TenantWrightMessagingProvider.MassTransit);
        var options = context.GetRequiredService<IOptions<MessagingOptions>>().Value;
        context.GetRequiredService<MassTransitLicenseResolver>().Apply(bus);
        bus.Host(options.RabbitMq.HostName, checked((ushort)options.RabbitMq.Port), options.RabbitMq.VirtualHost, host =>
        {
            host.Username(options.RabbitMq.UserName);
            host.Password(options.RabbitMq.Password);
            host.PublisherConfirmation = true;
            host.ConnectionName(options.ServiceName);
            if (options.RabbitMq.UseTls)
            {
                host.UseSsl(ssl =>
                {
                    ssl.ServerName = options.RabbitMq.HostName;
                    ssl.Certificate = X509Certificate2.CreateFromPemFile(
                        options.RabbitMq.ClientCertificatePath,
                        options.RabbitMq.ClientPrivateKeyPath);
                    if (!string.IsNullOrWhiteSpace(options.RabbitMq.TrustedCaCertificatePath))
                    {
                        ssl.CertificateValidationCallback =
                            RabbitMqServerCertificateTrust.CreateValidationCallback(
                                options.RabbitMq.TrustedCaCertificatePath);
                    }
                });
            }
        });

        bus.ConfigureJsonSerializerOptions(options => ConfigureJson(options, catalog));
        bus.UseSendFilter(typeof(TenantWrightSendTrustFilter<>), context);
        bus.UsePublishFilter(typeof(TenantWrightPublishTrustFilter<>), context);

        foreach (var route in catalog.Routes)
            ConfigureEntityName(bus, route.ContractType, ContractIdentity(route.ContractType));

        var attestations = context.GetRequiredService<TenantWrightAttestationService>();
        var timeProvider = context.GetRequiredService<TimeProvider>();
        foreach (var endpoint in catalog.ReceiveEndpoints)
        {
            bus.ReceiveEndpoint(endpoint.Name, receive =>
            {
                receive.Durable = true;
                receive.AutoDelete = false;
                receive.SetQuorumQueue();
                receive.PrefetchCount = options.RabbitMq.PrefetchCount;
                receive.ConcurrentMessageLimit = options.RabbitMq.ConcurrentMessageLimit;
                receive.ConfigureConsumeTopology = false;

                receive.ConfigureError(pipe => pipe.UseFilter(
                    new TenantWrightErrorTransportFilter(endpoint, attestations, timeProvider)));
                receive.ConfigureDeadLetter(pipe => pipe.UseFilter(
                    new TenantWrightDeadLetterTransportFilter(endpoint, attestations, timeProvider)));

                receive.UseConsumeFilter(typeof(TenantWrightRedeliveryTrustFilter<>), context);
                receive.UseQueueBasedDelayedRedelivery(redelivery =>
                {
                    redelivery.Ignore<MessagingSecurityException>();
                    redelivery.Ignore<OperationCanceledException>();
                    redelivery.Intervals(options.Delivery.DelayedRedeliverySeconds
                        .Select(value => TimeSpan.FromSeconds(value)).ToArray());
                });
                receive.UseMessageRetry(retry =>
                {
                    retry.Ignore<MessagingSecurityException>();
                    retry.Ignore<OperationCanceledException>();
                    retry.Intervals(options.Delivery.ImmediateRetryMilliseconds
                        .Select(value => TimeSpan.FromMilliseconds(value)).ToArray());
                });

                foreach (var contractType in endpoint.ContractTypes)
                    BindContract(receive, contractType);
                configureReceiveEndpoint?.Invoke(context, receive);
                foreach (var contractType in endpoint.ContractTypes)
                    receive.ConfigureConsumer(context, ConsumerType(contractType));
            });
        }
    }

    private static JsonSerializerOptions ConfigureJson(JsonSerializerOptions current, TenantWrightMessageCatalog catalog)
    {
        current.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        current.PropertyNameCaseInsensitive = false;
        current.WriteIndented = false;
        var resolvers = catalog.SerializationContexts.Cast<IJsonTypeInfoResolver>().ToList();
        resolvers.Add(TenantWrightContractJsonContext.Default);
        if (current.TypeInfoResolver is not null) resolvers.Add(current.TypeInfoResolver);
        current.TypeInfoResolver = JsonTypeInfoResolver.Combine(resolvers.ToArray());
        return current;
    }

    private static Type ConsumerType(Type contractType) =>
        typeof(TenantWrightMessageConsumer<>).MakeGenericType(contractType);

    private static string ContractIdentity(Type contractType) => contractType
        .GetCustomAttributes(typeof(Shared.Events.EventNameAttribute), false)
        .Cast<Shared.Events.EventNameAttribute>().Single().Name;

    private static void ConfigureEntityName(IRabbitMqBusFactoryConfigurator bus, Type contractType, string identity)
    {
        var method = typeof(TenantWrightMassTransitRegistration).GetMethod(nameof(ConfigureEntityNameGeneric), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(contractType);
        method.Invoke(null, [bus, identity]);
    }

    private static void ConfigureEntityNameGeneric<T>(IRabbitMqBusFactoryConfigurator bus, string identity) where T : class =>
        bus.Message<T>(topology => topology.SetEntityName(identity));

    private static void BindContract(IRabbitMqReceiveEndpointConfigurator endpoint, Type contractType)
    {
        var method = typeof(TenantWrightMassTransitRegistration).GetMethod(nameof(BindContractGeneric), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(contractType);
        method.Invoke(null, [endpoint]);
    }

    private static void BindContractGeneric<T>(IRabbitMqReceiveEndpointConfigurator endpoint) where T : class =>
        endpoint.Bind<T>();
}
