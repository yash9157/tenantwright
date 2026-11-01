using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shared.EventBus.Messaging;
using Xunit;

namespace Shared.EventBus.Tests;

public sealed class MessagingProviderConfigurationTests
{
    [Fact]
    public void Messaging_options_should_bind_from_the_provider_neutral_section()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Messaging:Provider"] = "None",
                ["Messaging:ServiceName"] = TenantWrightMessagingTopology.TenantPrincipal,
                ["Messaging:ProducerPrincipal"] = TenantWrightMessagingTopology.TenantPrincipal,
                ["Messaging:RabbitMq:HostName"] = "rabbitmq.internal",
                ["Messaging:RabbitMq:Port"] = "5671",
                ["Messaging:RabbitMq:VirtualHost"] = "tenantwright-dev",
                ["Messaging:Delivery:StopTimeout"] = "00:00:20",
                ["Messaging:Trust:ActiveKeyId"] = "tenantwright-tenant-v1",
                ["Messaging:Trust:PublicKeys:0:KeyId"] = "tenantwright-tenant-v1",
                ["Messaging:Trust:PublicKeys:0:Actor"] = TenantWrightMessagingTopology.TenantPrincipal,
                ["Messaging:Replay:MaximumDeathRecords"] = "12",
                // Sibling sections owned by persistence and by one provider must not disturb the neutral binding.
                ["Messaging:Outbox:BatchSize"] = "25",
                ["Messaging:MassTransit:Unused"] = "ignored"
            })
            .Build();

        var options = Resolve(configuration);

        Assert.Equal("Messaging", MessagingOptions.SectionName);
        Assert.Equal(TenantWrightMessagingProvider.None, options.Provider);
        Assert.Equal(TenantWrightMessagingTopology.TenantPrincipal, options.ServiceName);
        Assert.Equal(TenantWrightMessagingTopology.TenantPrincipal, options.ProducerPrincipal);
        Assert.Equal("rabbitmq.internal", options.RabbitMq.HostName);
        Assert.Equal(5671, options.RabbitMq.Port);
        Assert.Equal("tenantwright-dev", options.RabbitMq.VirtualHost);
        Assert.Equal(TimeSpan.FromSeconds(20), options.Delivery.StopTimeout);
        Assert.Equal("tenantwright-tenant-v1", options.Trust.ActiveKeyId);
        Assert.Equal(TenantWrightMessagingTopology.TenantPrincipal, Assert.Single(options.Trust.PublicKeys).Actor);
        Assert.Equal(12, options.Replay.MaximumDeathRecords);
    }

    [Fact]
    public void The_retired_mass_transit_section_should_no_longer_configure_messaging()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Messaging:MassTransit:ServiceName"] = TenantWrightMessagingTopology.TenantPrincipal
            })
            .Build();

        var options = Resolve(configuration);

        Assert.Equal(TenantWrightMessagingProvider.None, options.Provider);
        Assert.Equal(string.Empty, options.ServiceName);
        Assert.Equal(TenantWrightMessagingProvider.None, TenantWrightMessagingProviderConfiguration.Read(configuration));
    }

    [Theory]
    [InlineData(null, TenantWrightMessagingProvider.None)]
    [InlineData("", TenantWrightMessagingProvider.None)]
    [InlineData("  ", TenantWrightMessagingProvider.None)]
    [InlineData("None", TenantWrightMessagingProvider.None)]
    [InlineData("MassTransit", TenantWrightMessagingProvider.MassTransit)]
    [InlineData("masstransit", TenantWrightMessagingProvider.MassTransit)]
    [InlineData(" Wolverine ", TenantWrightMessagingProvider.Wolverine)]
    public void Provider_reader_should_accept_exactly_one_provider_name(string? configured, TenantWrightMessagingProvider expected)
    {
        Assert.Equal(expected, TenantWrightMessagingProviderConfiguration.Read(Configuration(configured)));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("MassTransit,Wolverine")]
    [InlineData("RabbitMq")]
    [InlineData("Mass-Transit")]
    public void Provider_reader_should_fail_closed_on_anything_else(string configured)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            TenantWrightMessagingProviderConfiguration.Read(Configuration(configured)));

        Assert.Contains("Messaging:Provider", exception.Message, StringComparison.Ordinal);
        Assert.Contains("None, MassTransit, Wolverine", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validator_should_reject_an_undefined_provider_value()
    {
        var validator = new MessagingOptionsValidator(new MessagingTestHostEnvironment(Environments.Production));

        var result = validator.Validate(null, new MessagingOptions { Provider = (TenantWrightMessagingProvider)99 });

        Assert.True(result.Failed);
    }

    [Theory]
    [InlineData(TenantWrightMessagingProvider.MassTransit)]
    [InlineData(TenantWrightMessagingProvider.Wolverine)]
    public void Validator_should_enforce_the_neutral_contract_for_every_real_provider(TenantWrightMessagingProvider provider)
    {
        var validator = new MessagingOptionsValidator(new MessagingTestHostEnvironment(Environments.Production));

        var result = validator.Validate(null, new MessagingOptions { Provider = provider });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.StartsWith("RabbitMq.HostName", StringComparison.Ordinal));
        Assert.Contains(result.Failures!, failure => failure.StartsWith("Trust.", StringComparison.Ordinal));
    }

    private static MessagingOptions Resolve(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHostEnvironment>(new MessagingTestHostEnvironment(Environments.Production));
        services.AddTenantWrightMessagingCore(configuration, TenantWrightMessagingTopology.CreateCatalog());
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<MessagingOptions>>().Value;
    }

    private static IConfiguration Configuration(string? provider) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [TenantWrightMessagingProviderConfiguration.ProviderKey] = provider
            })
            .Build();
}
