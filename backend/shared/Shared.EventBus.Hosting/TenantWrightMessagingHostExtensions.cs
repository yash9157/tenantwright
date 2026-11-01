using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shared.EntityFrameworkCore;
using Shared.EntityFrameworkCore.Messaging;
using Shared.EventBus.Messaging;

namespace Shared.EventBus.Hosting;

public static class TenantWrightMessagingHostExtensions
{
    /// <summary>
    /// Composes durable messaging for one service host with the transport selected by <c>Messaging:Provider</c>. The
    /// host consumes exactly the receive endpoints the reviewed topology assigns to <paramref name="ownerPrincipal"/>,
    /// its permission-cache endpoint included. Outbound messages go through the neutral outbox over
    /// <typeparamref name="TDbContext"/>; every inbound contract is verified, deduplicated in the neutral inbox and
    /// handled by its registered <see cref="ITenantWrightMessageHandler{TMessage}"/> inside one transactional unit of work.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Registration order is deliberate: the neutral core, then the provider (whose bus hosted service therefore starts
    /// before the outbox dispatcher and stops after it), then the durable outbox, inbox and their retention.
    /// </para>
    /// <para>
    /// <c>Messaging:Provider=None</c> (or an absent value) composes a bus-free host: no transport, no outbox
    /// dispatcher and no consumers. <see cref="NullTenantWrightMessageSender"/> drops events and fails commands closed.
    /// A provider that is not installed in this build, or a value that is not a provider, fails registration; there
    /// is never a fallback to another provider or to <c>None</c>.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddTenantWrightMessagingHost<TDbContext>(
        this IServiceCollection services,
        IConfiguration configuration,
        string ownerPrincipal)
        where TDbContext : DbContext, IEfCoreDbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var kind = TenantWrightMessagingProviderConfiguration.Read(configuration);
        // Resolved for every provider, None included, so an unapproved owner fails the same way on every deployment.
        var receiveEndpoints = TenantWrightMessagingTopology.ReceiveEndpointsOwnedBy(ownerPrincipal);

        if (kind == TenantWrightMessagingProvider.None)
        {
            services.Replace(ServiceDescriptor.Scoped<ITenantWrightMessageSender, NullTenantWrightMessageSender>());
            return services;
        }

        var provider = TenantWrightMessagingProviders.Resolve(kind);
        var catalog = TenantWrightMessagingTopology.CreateCatalog(
            serviceRoutes: null,
            serviceEndpointRoutes: null,
            receiveEndpoints);
        services.AddTenantWrightMessagingCore(configuration, catalog);
        provider.Register(services, configuration, catalog);
        services.AddTenantWrightMessagingPersistence<TDbContext>(configuration);
        return services;
    }
}
