using Shared.EventBus.Messaging;

namespace Shared.EventBus.Wolverine;

/// <summary>
/// The RabbitMQ names the Wolverine provider derives from the reviewed catalog. Endpoint owners declare everything;
/// a producer only publishes to an exchange that must already exist and be bound.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Every receive endpoint is a durable quorum queue named exactly as the catalog endpoint.</item>
/// <item>A command is published to the durable fanout exchange named by its destination; the destination's owner binds
/// that exchange to its queue.</item>
/// <item>An event is published to the durable fanout exchange named by its contract identity; every consuming
/// endpoint's owner binds that exchange to its own queue.</item>
/// <item>Every endpoint also binds its own exchange, including event endpoints, for protected replay to exactly that
/// endpoint rather than every subscriber.</item>
/// <item>Each endpoint queue dead-letters natively (RabbitMQ <c>x-dead-letter-exchange</c>, at-least-once) to its own
/// quorum <c>{endpoint}-dead-letter</c> queue through the exchange of the same name. Exhausted handler failures are
/// signed and confirmed to that exchange before acknowledgment; native rejection remains unsigned quarantine.</item>
/// </list>
/// The names match the MassTransit provider's endpoint, command and event exchange names. The dead-letter arguments
/// do not, so an endpoint queue a MassTransit host declared must be recreated before a Wolverine host can own it;
/// the broker rejects the inequivalent declaration and the host fails to start rather than consume from it.
/// </remarks>
internal static class TenantWrightWolverineTopology
{
    public const string DeadLetterSuffix = "-dead-letter";

    public static string DeadLetterQueueName(string endpointName) => endpointName + DeadLetterSuffix;

    /// <summary>The exchange a message of <paramref name="route"/> is published to and its consumers bind.</summary>
    public static string ExchangeFor(TenantWrightContractRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        return route.Kind switch
        {
            TenantWrightMessageRouteKind.Command => route.CommandDestination
                ?? throw new MessagingSecurityException(MessagingSecurityException.UntrustedCustody),
            TenantWrightMessageRouteKind.Event => TenantWrightMessageContracts.GetIdentity(route.ContractType),
            _ => throw new MessagingSecurityException(MessagingSecurityException.UnsupportedContract)
        };
    }
}
