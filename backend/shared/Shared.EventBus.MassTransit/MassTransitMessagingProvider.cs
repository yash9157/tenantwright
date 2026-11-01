using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shared.EventBus.Messaging;

namespace Shared.EventBus.MassTransit;

/// <summary>
/// The MassTransit 9 / RabbitMQ provider (<c>Messaging:Provider=MassTransit</c>). Registers the bus, its trust filters,
/// one consumer per receive-endpoint contract, the RabbitMQ custody verifiers, the readiness health check and the
/// <see cref="ITenantWrightMessagingTransport"/> the neutral outbox dispatches through. The commercial licence is read
/// from <c>MT_LICENSE</c> or <c>MT_LICENSE_PATH</c> only when this provider configures its bus.
/// </summary>
public sealed class MassTransitMessagingProvider : ITenantWrightMessagingProvider
{
    public TenantWrightMessagingProvider Kind => TenantWrightMessagingProvider.MassTransit;

    public void Register(IServiceCollection services, IConfiguration configuration, TenantWrightMessageCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(catalog);

        services.AddTenantWrightMassTransit(catalog);
    }
}
