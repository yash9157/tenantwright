using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shared.EntityFrameworkCore.Messaging;
using Shared.EventBus.Hosting;
using Shared.EventBus.MassTransit;
using Shared.EventBus.Messaging;
using Shared.EventBus.Wolverine;
using Wolverine.Runtime;
using Xunit;

namespace Shared.EntityFrameworkCore.Tests;

/// <summary>
/// Container-free proofs that <see cref="TenantWrightMessagingHostExtensions.AddTenantWrightMessagingHost{TDbContext}"/>
/// composes exactly the provider <c>Messaging:Provider</c> names and fails closed for anything else.
/// </summary>
public sealed class TenantWrightMessagingProviderSelectionTests
{
    [Theory]
    [InlineData("None")]
    [InlineData("none")]
    [InlineData("")]
    [InlineData(null)]
    public void None_or_absent_provider_composes_a_bus_free_host(string? provider)
    {
        var services = ComposeHost(provider);

        Assert.Equal(
            typeof(NullTenantWrightMessageSender),
            Assert.Single(services, descriptor => descriptor.ServiceType == typeof(ITenantWrightMessageSender)).ImplementationType);
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(ITenantWrightMessagingTransport));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IHostedService));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IBus));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(TenantWrightMessageCatalog));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(ITenantWrightInboxTransaction));
    }

    [Fact]
    public void Mass_transit_provider_registers_its_transport_and_the_neutral_durable_messaging_once()
    {
        var services = ComposeHost("MassTransit");

        Assert.Equal(
            typeof(MassTransitMessagingTransport),
            Assert.Single(services, descriptor => descriptor.ServiceType == typeof(ITenantWrightMessagingTransport)).ImplementationType);
        Assert.Equal(
            typeof(TenantWrightOutboxMessageSender<SelectionProbeDbContext>),
            Assert.Single(services, descriptor => descriptor.ServiceType == typeof(ITenantWrightMessageSender)).ImplementationType);
        Assert.Equal(
            typeof(TenantWrightInboxTransaction<SelectionProbeDbContext>),
            Assert.Single(services, descriptor => descriptor.ServiceType == typeof(ITenantWrightInboxTransaction)).ImplementationType);
        Assert.Single(services, descriptor => descriptor.ImplementationType == typeof(TenantWrightOutboxDispatcher<SelectionProbeDbContext>));
        Assert.Single(services, descriptor => descriptor.ImplementationType == typeof(TenantWrightInboxRetentionService<SelectionProbeDbContext>));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(ITenantWrightRedeliveryCustodyVerifier));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(ITenantWrightReplayCustodyVerifier));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IProtectedReplayIdempotencyStore));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(TenantWrightMessageCatalog));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IValidateOptions<MessagingOptions>));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IBus));
    }

    [Fact]
    public void Mass_transit_bus_hosted_service_is_registered_before_the_outbox_dispatcher()
    {
        var hostedServices = ComposeHost("MassTransit")
            .Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
            .ToList();

        var dispatcher = hostedServices.FindIndex(descriptor =>
            descriptor.ImplementationType == typeof(TenantWrightOutboxDispatcher<SelectionProbeDbContext>));
        var firstTransportService = hostedServices.FindIndex(descriptor =>
            descriptor.ImplementationType?.Namespace?.StartsWith("Shared.", StringComparison.Ordinal) != true);

        Assert.InRange(firstTransportService, 0, dispatcher - 1);
    }

    [Fact]
    public void Wolverine_provider_registers_its_transport_and_the_neutral_durable_messaging_once()
    {
        var services = ComposeHost("Wolverine");

        Assert.Equal(
            typeof(WolverineMessagingTransport),
            Assert.Single(services, descriptor => descriptor.ServiceType == typeof(ITenantWrightMessagingTransport)).ImplementationType);
        Assert.Equal(
            typeof(TenantWrightOutboxMessageSender<SelectionProbeDbContext>),
            Assert.Single(services, descriptor => descriptor.ServiceType == typeof(ITenantWrightMessageSender)).ImplementationType);
        Assert.Equal(
            typeof(TenantWrightInboxTransaction<SelectionProbeDbContext>),
            Assert.Single(services, descriptor => descriptor.ServiceType == typeof(ITenantWrightInboxTransaction)).ImplementationType);
        Assert.Single(services, descriptor => descriptor.ImplementationType == typeof(TenantWrightOutboxDispatcher<SelectionProbeDbContext>));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IWolverineRuntime));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(TenantWrightMessageCatalog));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IValidateOptions<MessagingOptions>));

        // Wolverine accepts protected replay, but broker-delayed redelivery custody stays rejected.
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(ITenantWrightRedeliveryCustodyVerifier));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(ITenantWrightReplayCustodyVerifier));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IProtectedReplayTransport));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IProtectedReplayIdempotencyStore));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IBus));
    }

    [Fact]
    public void Wolverine_runtime_hosted_service_is_registered_before_the_outbox_dispatcher()
    {
        var hostedServices = ComposeHost("Wolverine")
            .Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
            .ToList();

        var dispatcher = hostedServices.FindIndex(descriptor =>
            descriptor.ImplementationType == typeof(TenantWrightOutboxDispatcher<SelectionProbeDbContext>));
        var firstTransportService = hostedServices.FindIndex(descriptor =>
            descriptor.ImplementationType?.Namespace?.StartsWith("Shared.", StringComparison.Ordinal) != true);

        Assert.InRange(firstTransportService, 0, dispatcher - 1);
    }

    [Theory]
    [InlineData("RabbitMq")]
    [InlineData("1")]
    [InlineData("MassTransit, Wolverine")]
    [InlineData("Mass Transit")]
    public void Unknown_provider_value_fails_closed(string provider)
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddTenantWrightMessagingHost<SelectionProbeDbContext>(
                Configuration(provider), TenantWrightMessagingTopology.AuthPrincipal));

        Assert.Contains("is not a messaging provider", exception.Message, StringComparison.Ordinal);
        Assert.Empty(services);
    }

    [Fact]
    public void Unapproved_owner_fails_even_on_a_bus_free_host()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddTenantWrightMessagingHost<SelectionProbeDbContext>(
                Configuration("None"), "tenantwright-orders"));

        Assert.Equal("Receive endpoints require an approved service owner.", exception.Message);
    }

    private static ServiceCollection ComposeHost(string? provider)
    {
        var services = new ServiceCollection();
        services.AddTenantWrightMessagingHost<SelectionProbeDbContext>(
            Configuration(provider), TenantWrightMessagingTopology.AuthPrincipal);
        return services;
    }

    private static IConfiguration Configuration(string? provider) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [TenantWrightMessagingProviderConfiguration.ProviderKey] = provider,
                [$"{MessagingOptions.SectionName}:ServiceName"] = TenantWrightMessagingTopology.AuthPrincipal
            })
            .Build();

    [Fact]
    public void Every_provider_kind_except_none_is_installed()
    {
        foreach (var kind in Enum.GetValues<TenantWrightMessagingProvider>().Where(kind => kind != TenantWrightMessagingProvider.None))
        {
            var services = ComposeHost(kind.ToString());
            Assert.Single(services, descriptor => descriptor.ServiceType == typeof(ITenantWrightMessagingTransport));
        }
    }

    /// <summary>Only ever used to satisfy the generic parameter while inspecting registrations.</summary>
    internal sealed class SelectionProbeDbContext(DbContextOptions<SelectionProbeDbContext> options)
        : SharedDbContext<SelectionProbeDbContext>(options);
}
