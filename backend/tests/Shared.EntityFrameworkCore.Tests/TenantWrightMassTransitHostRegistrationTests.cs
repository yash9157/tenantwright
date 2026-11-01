using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shared.EventBus.Hosting;
using Shared.EventBus.MassTransit;
using Shared.EventBus.Messaging;
using Xunit;

namespace Shared.EntityFrameworkCore.Tests;

/// <summary>
/// Container-free proofs that a service host's MassTransit composition is derived from the reviewed topology: it
/// consumes exactly the endpoints its principal owns and registers the generic consumer for every contract on them.
/// </summary>
public sealed class TenantWrightMassTransitHostRegistrationTests
{
    public static TheoryData<string> ServicePrincipals { get; } = new()
    {
        TenantWrightMessagingTopology.AuditLogsPrincipal,
        TenantWrightMessagingTopology.AuthPrincipal,
        TenantWrightMessagingTopology.IdentityPrincipal,
        TenantWrightMessagingTopology.NotificationsPrincipal,
        TenantWrightMessagingTopology.PermissionsPrincipal,
        TenantWrightMessagingTopology.TenantPrincipal,
        TenantWrightMessagingTopology.WorkflowsPrincipal
    };

    [Theory]
    [MemberData(nameof(ServicePrincipals))]
    public void Host_receive_endpoints_should_equal_the_topology_endpoints_owned_by_its_principal(string principal)
    {
        var services = BuildHostServices(principal);

        var catalog = RegisteredCatalog(services);
        var expected = TenantWrightMessagingTopology.CreateCatalog().EndpointRoutes
            .Where(endpoint => string.Equals(endpoint.OwnerPrincipal, principal, StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(
            expected.Select(endpoint => endpoint.Name).Order(StringComparer.Ordinal),
            catalog.ReceiveEndpoints.Select(endpoint => endpoint.Name).Order(StringComparer.Ordinal));
        Assert.All(catalog.ReceiveEndpoints, endpoint =>
        {
            Assert.Equal(principal, endpoint.OwnerPrincipal);
            Assert.Equal(catalog.GetEndpointRoute(endpoint.Name).ContractTypes, endpoint.ContractTypes);
        });
        Assert.Contains(
            catalog.ReceiveEndpoints,
            endpoint => endpoint.Name == TenantWrightMessagingTopology.PermissionCacheEndpoint(principal));
    }

    [Theory]
    [MemberData(nameof(ServicePrincipals))]
    public void Host_registration_should_register_the_generic_consumer_for_every_endpoint_contract(string principal)
    {
        var services = BuildHostServices(principal);

        var contracts = RegisteredCatalog(services).ReceiveEndpoints
            .SelectMany(endpoint => endpoint.ContractTypes)
            .Distinct()
            .ToArray();

        Assert.NotEmpty(contracts);
        Assert.All(contracts, contract =>
        {
            var consumerType = typeof(TenantWrightMessageConsumer<>).MakeGenericType(contract);
            Assert.Contains(services, descriptor => descriptor.ServiceType == consumerType);
        });

        // No consumer is registered for a contract this host does not consume.
        var registeredConsumerContracts = services
            .Select(descriptor => descriptor.ServiceType)
            .Where(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(TenantWrightMessageConsumer<>))
            .Select(type => type.GetGenericArguments()[0])
            .ToHashSet();
        Assert.Equal(contracts.ToHashSet(), registeredConsumerContracts);
    }

    [Fact]
    public void Unapproved_host_principal_should_fail_closed_under_the_mass_transit_provider()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => BuildHostServices("tenantwright-orders"));

        Assert.Equal("Receive endpoints require an approved service owner.", exception.Message);
    }

    private static ServiceCollection BuildHostServices(string principal)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [TenantWrightMessagingProviderConfiguration.ProviderKey] = "MassTransit",
                [$"{MessagingOptions.SectionName}:ServiceName"] = principal
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTenantWrightMessagingHost<RegistrationProbeDbContext>(configuration, principal);
        return services;
    }

    private static TenantWrightMessageCatalog RegisteredCatalog(IServiceCollection services) =>
        Assert.IsType<TenantWrightMessageCatalog>(Assert.Single(
            services,
            descriptor => descriptor.ServiceType == typeof(TenantWrightMessageCatalog)).ImplementationInstance);

    /// <summary>Only ever used to satisfy the generic parameter while inspecting registrations.</summary>
    internal sealed class RegistrationProbeDbContext(Microsoft.EntityFrameworkCore.DbContextOptions<RegistrationProbeDbContext> options)
        : SharedDbContext<RegistrationProbeDbContext>(options);
}
