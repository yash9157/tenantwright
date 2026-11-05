using System.Reflection;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using Shared.EventBus.Messaging;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.RabbitMQ;
using Wolverine.RabbitMQ.Internal;
using ExchangeType = Wolverine.RabbitMQ.ExchangeType;

namespace Shared.EventBus.Wolverine;

internal static class TenantWrightWolverineRegistration
{
    /// <summary>
    /// Registers Wolverine as the RabbitMQ consumer runtime over the neutral messaging core for
    /// <paramref name="catalog"/>, plus <see cref="WolverineMessagingTransport"/> for confirmed outbox dispatch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Wolverine is confined to consumption. It persists nothing (no message store, durability agent off, no inbox,
    /// outbox or transactional middleware), discovers nothing (conventional discovery is off and the static type
    /// load mode makes any accidentally discovered handler a startup failure), routes nothing locally and never sends:
    /// its sending connection, reply queue and global dead-letter queue are all disabled. It is registered through
    /// <c>IServiceCollection.AddWolverine</c> with manual extension discovery, so it neither scans assemblies for
    /// extensions nor takes over the host's command line.
    /// </para>
    /// <para>
    /// Every catalog receive endpoint becomes a quorum queue consumed inline with the configured prefetch and
    /// concurrency, so a delivery is acknowledged only after the neutral pipeline completed. A trust rejection,
    /// unknown contract or undeserializable payload is dead-lettered at once; a failure the handler marks
    /// <see cref="ITenantWrightTransientMessageFailure"/> is retried in process after <see cref="TransientRetryCooldowns"/>;
    /// any other failure is retried in process at <c>Messaging:Delivery:ImmediateRetryMilliseconds</c>; both are then
    /// signed and confirmed to the dead-letter exchange. An authorized replay permits one attempt before terminal
    /// custody. Blocked terminal publication retains the unacknowledged delivery; host shutdown leaves it to the broker.
    /// Settings are read from <paramref name="configuration"/> here because Wolverine is
    /// configured at registration; <see cref="MessagingOptionsValidator"/> validates the same section at startup.
    /// </para>
    /// <para>
    /// The neutral core (<see cref="TenantWrightMessagingRegistration.AddTenantWrightMessagingCore"/>) must already be
    /// registered, and the host's persistence layer must register the durable <see cref="ITenantWrightInboxTransaction"/>
    /// and <see cref="IProtectedReplayIdempotencyStore"/>; the runtime fails to start without them.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddTenantWrightWolverine(
        this IServiceCollection services,
        IConfiguration configuration,
        TenantWrightMessageCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(catalog);

        var settings = configuration.GetSection(MessagingOptions.SectionName).Get<MessagingOptions>() ?? new MessagingOptions();

        // Wolverine resolves its connection factory from the container when it connects, after options validation.
        services.AddSingleton<IConnectionFactory>(provider => RabbitMqConnectionFactories.Create(
            provider.GetRequiredService<IOptions<MessagingOptions>>().Value, automaticRecovery: true));
        services.AddSingleton<RabbitMqConfirmedPublisher>();
        services.AddSingleton<ITenantWrightMessagingTransport, WolverineMessagingTransport>();
        services.AddSingleton<WolverineTerminalPublisher>();
        services.AddSingleton<WolverineTerminalMessageReader>();
        services.AddSingleton<IProtectedReplayTransport, WolverineProtectedReplayTransport>();
        services.AddSingleton<ITenantWrightReplayCustodyVerifier, WolverineReplayCustodyVerifier>();
        services.AddSingleton<ILogger<HandlerDiscovery>, WolverineDiscoveryLogger>();
        services.AddAsyncWolverineExtension<WolverineStartupVerification>();
        services.AddHealthChecks().AddCheck<WolverineMessagingHealthCheck>(
            WolverineMessagingHealthCheck.Name,
            HealthStatus.Unhealthy,
            ["ready"]);

        services.AddWolverine(ExtensionDiscovery.ManualOnly, options => Configure(options, settings, catalog));
        return services;
    }

    private static void Configure(WolverineOptions options, MessagingOptions settings, TenantWrightMessageCatalog catalog)
    {
        options.ApplicationAssembly = typeof(TenantWrightWolverineRegistration).Assembly;
        if (!string.IsNullOrWhiteSpace(settings.ServiceName))
            options.ServiceName = settings.ServiceName;

        options.Discovery.DisableConventionalDiscovery();
        options.CodeGeneration.TypeLoadMode = TypeLoadMode.Static;
        // Solo: listeners start on every replica (the serverless modes never start listeners) and, with no message
        // store and the durability agent off, there is no node registration, leader election or envelope recovery.
        options.Durability.Mode = DurabilityMode.Solo;
        options.Durability.DurabilityAgentEnabled = false;
        options.Policies.DisableConventionalLocalRouting();
        options.UnknownMessageBehavior = UnknownMessageBehavior.DeadLetterQueue;
        options.EnableAutomaticFailureAcks = false;

        ConfigureFailurePolicy(options, settings.Delivery);
        ConfigureTopology(options, settings.RabbitMq, catalog);

        var register = typeof(TenantWrightWolverineRegistration).GetMethod(
            nameof(RegisterHandler), BindingFlags.NonPublic | BindingFlags.Static)!;
        foreach (var contractType in catalog.ReceiveEndpoints.SelectMany(endpoint => endpoint.ContractTypes).Distinct())
            register.MakeGenericMethod(contractType).Invoke(null, [options]);
    }

    /// <summary>
    /// In-process cooldowns for failures a handler marks <see cref="ITenantWrightTransientMessageFailure"/>: about two
    /// minutes in total, long enough for a tenant cache to converge after the Tenant service committed a change.
    /// </summary>
    /// <remarks>
    /// Wolverine's <c>ScheduleRetry</c> needs native broker scheduling or a durable message store; this adapter has
    /// neither (RabbitMQ has no native scheduling here and the store is off), so a scheduled retry would have nowhere to
    /// live. The cooldown therefore runs inline: the delivery stays unacknowledged and holds one of the endpoint's
    /// <c>ConcurrentMessageLimit</c> consumer slots for at most the sum of these delays, while the endpoint's other
    /// slots and every other endpoint keep consuming. A host shutdown during a cooldown leaves the delivery to the
    /// broker, as for any other in-flight message.
    /// </remarks>
    internal static readonly TimeSpan[] TransientRetryCooldowns =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60)
    ];

    /// <summary>Rules are evaluated in order; the first that matches decides.</summary>
    private static void ConfigureFailurePolicy(WolverineOptions options, MessagingDeliveryOptions delivery)
    {
        options.OnException<TenantWrightConsumeInterruptedException>().CustomActionIndefinitely(
            static (_, _, _) => ValueTask.CompletedTask,
            "Leave the delivery unsettled for broker redelivery after the host stopped");
        options.OnException<MessagingSecurityException>().MoveToErrorQueue();
        options.OnException<WolverineReplayAttemptFailedException>().CustomActionIndefinitely(
            WolverineTerminalFailure.ExecuteAsync, "Record signed terminal custody for a failed replay attempt");
        options.OnException(TenantWrightMessageFailureClassifier.IsTransient)
            .RetryWithCooldown(TransientRetryCooldowns)
            .Then.CustomActionIndefinitely(WolverineTerminalFailure.ExecuteAsync, "Record signed terminal custody");
        options.OnAnyException()
            .RetryWithCooldown(delivery.ImmediateRetryMilliseconds.Select(value => TimeSpan.FromMilliseconds(value)).ToArray())
            .Then.CustomActionIndefinitely(WolverineTerminalFailure.ExecuteAsync, "Record signed terminal custody");
    }

    private static void ConfigureTopology(
        WolverineOptions options,
        MessagingRabbitMqOptions rabbitSettings,
        TenantWrightMessageCatalog catalog)
    {
        // Disable the transport-wide dead-letter queue before any queue exists: queues copy it when they are created.
        var rabbit = options.UseRabbitMq()
            .DisableDeadLetterQueueing()
            .DisableSystemRequestReplyQueueDeclaration()
            .UseListenerConnectionOnly()
            .AutoProvision();

        var prefetch = Math.Clamp(rabbitSettings.PrefetchCount, (ushort)1, (ushort)1024);
        var concurrency = Math.Clamp(rabbitSettings.ConcurrentMessageLimit, (ushort)1, prefetch);
        foreach (var endpoint in catalog.ReceiveEndpoints)
        {
            options.ListenToRabbitQueue(endpoint.Name, queue =>
                {
                    queue.IsDurable = true;
                    queue.AutoDelete = false;

                    // At-least-once dead-lettering keeps a dead-lettered message until its dead-letter queue confirms it;
                    // RabbitMQ requires reject-publish overflow for that strategy.
                    queue.Arguments["x-dead-letter-strategy"] = "at-least-once";
                    queue.Arguments["x-overflow"] = "reject-publish";
                })
                .QueueType(QueueType.quorum)
                .ProcessInline()
                .PreFetchCount(prefetch)
                .ConsumerDispatchConcurrency(concurrency)
                .DeadLetterQueueing(new DeadLetterQueue(
                    TenantWrightWolverineTopology.DeadLetterQueueName(endpoint.Name),
                    DeadLetterQueueMode.Native)
                {
                    ConfigureQueue = static deadLetter =>
                    {
                        deadLetter.QueueType = QueueType.quorum;
                        deadLetter.IsDurable = true;
                    }
                })
                .UseInterop(new TenantWrightRabbitMqEnvelopeMapper(endpoint, catalog));

            foreach (var exchange in endpoint.ContractTypes
                         .Select(contractType => TenantWrightWolverineTopology.ExchangeFor(catalog.GetRoute(contractType)))
                         .Append(endpoint.Name)
                         .Distinct(StringComparer.Ordinal))
            {
                rabbit.BindExchange(exchange, ExchangeType.Fanout).ToQueue(endpoint.Name, string.Empty);
            }
        }
    }

    private static void RegisterHandler<TMessage>(WolverineOptions options) where TMessage : class =>
        options.AddMessageHandler(new TenantWrightWolverineMessageHandler<TMessage>());
}
