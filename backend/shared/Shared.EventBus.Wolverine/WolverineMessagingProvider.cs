using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shared.EventBus.Messaging;

namespace Shared.EventBus.Wolverine;

/// <summary>
/// The Wolverine / RabbitMQ provider (<c>Messaging:Provider=Wolverine</c>). Wolverine runs only as the RabbitMQ consumer
/// runtime: one pre-built handler per receive-endpoint contract adapts each delivery into the neutral
/// <see cref="TenantWrightInboundMessagePipeline"/>, acknowledged inline after the pipeline completes. Outbound dispatch
/// does not go through Wolverine: <see cref="WolverineMessagingTransport"/> publishes with RabbitMQ.Client directly,
/// with mandatory routing and publisher confirms.
/// </summary>
/// <remarks>
/// The provider supplies signed terminal custody and protected replay. Broker-level delayed redelivery remains
/// unsupported: no redelivery custody verifier is registered, and unsigned native quarantine is never replay authority.
/// </remarks>
public sealed class WolverineMessagingProvider : ITenantWrightMessagingProvider
{
    public TenantWrightMessagingProvider Kind => TenantWrightMessagingProvider.Wolverine;

    public void Register(IServiceCollection services, IConfiguration configuration, TenantWrightMessageCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(catalog);

        services.AddTenantWrightWolverine(configuration, catalog);
    }
}
