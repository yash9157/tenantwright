using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Shared.AspNetCore.Host;
using Shared.AspNetCore.Host.Middleware;
using Shared.Modularity.DependencyInjection;
using Xunit;

namespace Shared.MultiTenancy.Tests;

public class ConventionalHostMiddlewareRegistrationTests
{
    [Fact]
    public void RegisterAssembly_should_not_register_unkeyed_IMiddleware_descriptors_for_host_middlewares()
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
                typeof(SharedAspNetCoreHostModule).Assembly,
                options));

        Assert.Null(exception);
        Assert.Empty(services.Where(x =>
            x.ServiceType == typeof(IMiddleware) &&
            x.ServiceKey == null));

        AssertSingleTransientSelfRegistration<MultiTenancyMiddleware>(services);
        AssertSingleTransientSelfRegistration<SessionValidationMiddleware>(services);
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
