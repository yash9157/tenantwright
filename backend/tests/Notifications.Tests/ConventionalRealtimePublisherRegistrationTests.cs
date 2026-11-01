using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Notifications.Application;
using Notifications.Application.Contracts.Realtime;
using Notifications.Application.Realtime;
using NotificationsHost = Notifications.NotificationsHostModule;
using Shared.Modularity.Context;
using Shared.Modularity.DependencyInjection;
using Xunit;

namespace Notifications.Tests;

public class ConventionalRealtimePublisherRegistrationTests
{
    [Fact]
    public void RegisterAssembly_should_not_register_unkeyed_INotificationsRealtimePublisher_descriptors_for_realtime_publishers()
    {
        var services = new ServiceCollection();
        var options = new ConventionalRegistrationOptions
        {
            ExposureMode = ConventionalServiceExposureMode.Default,
            FailOnDuplicateRegistrations = true
        };

        var exception = Record.Exception(() =>
        {
            ConventionalRegistrar.RegisterAssembly(
                services,
                typeof(NotificationsApplicationModule).Assembly,
                options);

            ConventionalRegistrar.RegisterAssembly(
                services,
                typeof(NotificationsHost).Assembly,
                options);
        });

        Assert.Null(exception);
        Assert.Empty(services.Where(x =>
            x.ServiceType == typeof(INotificationsRealtimePublisher) &&
            x.ServiceKey == null));
    }

    [Fact]
    public void NotificationsApplicationModule_should_register_noop_realtime_publisher_as_default()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();
        var context = new ServiceConfigurationContext(services, configuration);

        new NotificationsApplicationModule().ConfigureServices(context);

        var descriptor = Assert.Single(services.Where(x =>
            x.ServiceType == typeof(INotificationsRealtimePublisher) &&
            x.ServiceKey == null));

        Assert.Equal(ServiceLifetime.Transient, descriptor.Lifetime);
        Assert.Equal(typeof(NoopNotificationsRealtimePublisher), descriptor.ImplementationType);
    }
}
