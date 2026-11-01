using Microsoft.Extensions.DependencyInjection;
using Shared.EventBus.Messaging;
using Shared.Modularity.DependencyInjection;
using Shared.Permissions;
using Xunit;

namespace Tenant.Tests;

/// <summary>
/// The Tenant host consumes exactly the receive endpoints the topology assigns to its principal. Every contract on
/// those endpoints must resolve exactly one transport-neutral handler from the host's conventional composition, or
/// the generic MassTransit consumer cannot be activated when the message arrives.
/// </summary>
public sealed class TenantMessageHandlerRegistrationTests
{
    [Fact]
    public void Every_contract_on_the_host_receive_endpoints_should_resolve_one_transient_message_handler()
    {
        var services = new ServiceCollection();
        var options = new ConventionalRegistrationOptions
        {
            ExposureMode = ConventionalServiceExposureMode.Default,
            FailOnDuplicateRegistrations = true
        };
        ConventionalRegistrar.RegisterAssembly(services, typeof(global::Tenant.TenantHostModule).Assembly, options);
        ConventionalRegistrar.RegisterAssembly(services, typeof(global::Tenant.Application.TenantApplicationModule).Assembly, options);
        ConventionalRegistrar.RegisterAssembly(services, typeof(SharedPermissionsModule).Assembly, options);
        using var provider = services.BuildServiceProvider();
        var registrations = provider.GetRequiredService<IServiceProviderIsService>();

        var contracts = TenantWrightMessagingTopology.ReceiveEndpointsOwnedBy(TenantWrightMessagingTopology.TenantPrincipal)
            .SelectMany(endpoint => endpoint.ContractTypes)
            .Distinct()
            .ToArray();

        Assert.Superset(TenantWrightMessagingTopology.PermissionCacheContracts.ToHashSet(), contracts.ToHashSet());
        Assert.All(contracts, contract =>
        {
            var handlerType = typeof(ITenantWrightMessageHandler<>).MakeGenericType(contract);
            var descriptor = Assert.Single(services, candidate => candidate.ServiceType == handlerType);

            Assert.Equal(ServiceLifetime.Transient, descriptor.Lifetime);
            Assert.NotNull(descriptor.ImplementationType);
            Assert.True(handlerType.IsAssignableFrom(descriptor.ImplementationType));
            Assert.True(registrations.IsService(handlerType));
        });
    }
}