using Microsoft.Extensions.DependencyInjection;
using Shared.Modularity.DependencyInjection;
using Shared.Modularity.Extensions;
using Xunit;

namespace Shared.Modularity.Tests;

public class ConventionalRegistrarTests
{
    [Fact]
    public void ReplaceServices_should_replace_existing_service_registration()
    {
        var services = new ServiceCollection();
        services.AddTransient<IReplaceService, ExistingReplaceService>();

        ConventionalRegistrar.RegisterAssembly(
            services,
            typeof(ConventionalRegistrarTests).Assembly,
            new ConventionalRegistrationOptions
            {
                ExposureMode = ConventionalServiceExposureMode.Default
            });

        var registrations = services.Where(x => x.ServiceType == typeof(IReplaceService)).ToList();
        Assert.Single(registrations);
        Assert.Equal(typeof(ReplacementReplaceService), registrations[0].ImplementationType);
    }

    [Fact]
    public void TryRegister_should_not_override_existing_service_registration()
    {
        var services = new ServiceCollection();
        services.AddTransient<ITryService, ExistingTryService>();

        ConventionalRegistrar.RegisterAssembly(
            services,
            typeof(ConventionalRegistrarTests).Assembly,
            new ConventionalRegistrationOptions
            {
                ExposureMode = ConventionalServiceExposureMode.Default
            });

        var registrations = services.Where(x => x.ServiceType == typeof(ITryService)).ToList();
        Assert.Single(registrations);
        Assert.Equal(typeof(ExistingTryService), registrations[0].ImplementationType);
    }

    [Fact]
    public void Default_should_expose_default_interface_and_self()
    {
        var services = new ServiceCollection();

        ConventionalRegistrar.RegisterAssembly(
            services,
            typeof(ConventionalRegistrarTests).Assembly,
            new ConventionalRegistrationOptions
            {
                ExposureMode = ConventionalServiceExposureMode.Default
            });

        Assert.Contains(services, x =>
            x.ServiceType == typeof(IMatchingAppService) &&
            x.ImplementationType == typeof(MatchingAppService));

        Assert.Contains(services, x =>
            x.ServiceType == typeof(MatchingAppService) &&
            x.ImplementationType == typeof(MatchingAppService));
    }

    [Fact]
    public void DefaultWithLegacyAliases_should_register_legacy_aliases_and_report_them()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ConventionalRegistrationReport());

        ConventionalRegistrar.RegisterAssembly(
            services,
            typeof(ConventionalRegistrarTests).Assembly,
            new ConventionalRegistrationOptions
            {
                ExposureMode = ConventionalServiceExposureMode.DefaultWithLegacyAliases,
                FailOnLegacyExposureMode = false
            });

        Assert.Contains(services, x =>
            x.ServiceType == typeof(ILegacyAliasContract));

        var report = services.GetSingletonInstance<ConventionalRegistrationReport>();
        Assert.Contains(report.LegacyAliases, x =>
            x.ImplementationType == typeof(LegacyAliasWorker) &&
            x.ServiceType == typeof(ILegacyAliasContract));
    }

    public interface IReplaceService
    {
    }

    public sealed class ExistingReplaceService : IReplaceService
    {
    }

    [Dependency(ServiceLifetime.Transient, ReplaceServices = true)]
    [ExposeServices(typeof(IReplaceService))]
    public sealed class ReplacementReplaceService : IReplaceService
    {
    }

    public interface ITryService
    {
    }

    public sealed class ExistingTryService : ITryService
    {
    }

    [Dependency(ServiceLifetime.Transient, TryRegister = true)]
    [ExposeServices(typeof(ITryService))]
    public sealed class TryCandidateService : ITryService
    {
    }

    public interface IMatchingAppService
    {
    }

    public sealed class MatchingAppService : IMatchingAppService, ITransientDependency
    {
    }

    public interface ILegacyAliasContract
    {
    }

    public sealed class LegacyAliasWorker : ILegacyAliasContract, ITransientDependency
    {
    }
}
