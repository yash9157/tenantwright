using Microsoft.Extensions.DependencyInjection;
using Shared.AuditLogs;
using Shared.AuditLogs.Auditing;
using Shared.Modularity.DependencyInjection;
using Shared.Uow;
using Shared.Uow.Implementations;
using Xunit;

namespace Shared.AuditLogs.Tests;

public class ConventionalInterceptorRegistrationTests
{
    [Fact]
    public void RegisterAssembly_should_not_register_unkeyed_IInterceptor_descriptors_for_uow_and_auditing()
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
                typeof(UowModule).Assembly,
                options);

            ConventionalRegistrar.RegisterAssembly(
                services,
                typeof(SharedAuditLogsModule).Assembly,
                options);
        });

        Assert.Null(exception);
        Assert.Empty(services.Where(x =>
            x.ServiceType.FullName == "Castle.DynamicProxy.IInterceptor" &&
            x.ServiceKey == null));

        AssertSingleTransientSelfRegistration<UnitOfWorkInterceptor>(services);
        AssertSingleTransientSelfRegistration<AuditingInterceptor>(services);
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
