using Microsoft.Extensions.DependencyInjection;
using Shared.Modularity.DependencyInjection;
using Xunit;

namespace Shared.MultiTenancy.Tests;

public class ConventionalTenantContributorRegistrationTests
{
    [Fact]
    public void RegisterAssembly_should_not_register_unkeyed_ITenantResolveContributor_descriptors()
    {
        var services = new ServiceCollection();

        var options = new ConventionalRegistrationOptions
        {
            ExposureMode = ConventionalServiceExposureMode.Default,
            FailOnDuplicateRegistrations = true
        };

        var exception = Record.Exception(() =>
            ConventionalRegistrar.RegisterAssembly(
                services,
                typeof(MultiTenancyModule).Assembly,
                options));

        Assert.Null(exception);
        Assert.Empty(services.Where(x =>
            x.ServiceType == typeof(ITenantResolveContributor) &&
            x.ServiceKey == null));

        AssertSingleTransientSelfRegistration<CurrentUserTenantResolveContributor>(services);
        AssertSingleTransientSelfRegistration<HeaderTenantResolveContributor>(services);
    }

    private static void AssertSingleTransientSelfRegistration<TService>(IServiceCollection services)
        where TService : class
    {
        var descriptor = Assert.Single(services.Where(x =>
            x.ServiceType == typeof(TService) &&
            x.ServiceKey == null));

        Assert.Equal(ServiceLifetime.Transient, descriptor.Lifetime);
        Assert.Equal(typeof(TService), descriptor.NormalizedImplementationType());
    }
}
