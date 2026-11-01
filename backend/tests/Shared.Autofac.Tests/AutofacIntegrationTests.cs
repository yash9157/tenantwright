using Autofac.Extensions.DependencyInjection;
using Castle.DynamicProxy;
using Microsoft.Extensions.DependencyInjection;
using Shared.Autofac;
using Shared.Modularity.DependencyInjection;
using Xunit;

namespace Shared.Autofac.Tests;

public class AutofacIntegrationTests
{
    [Fact]
    public void Provider_should_register_service_provider_interfaces_and_keyed_services()
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IKeyedContract, AlphaKeyedContract>("alpha");
        services.AddKeyedTransient<IKeyedContract>("factory", (_, _) => new FactoryKeyedContract());
        services.AddTransient<IKeyedConsumer, KeyedConsumer>();

        using var provider = BuildProvider(services);

        var isService = provider.GetRequiredService<IServiceProviderIsService>();
        Assert.True(isService.IsService(typeof(IKeyedConsumer)));

        var keyedProvider = provider.GetRequiredService<IKeyedServiceProvider>();
        var alpha = keyedProvider.GetRequiredKeyedService<IKeyedContract>("alpha");
        var factory = keyedProvider.GetRequiredKeyedService<IKeyedContract>("factory");

        Assert.Equal("alpha", alpha.Name);
        Assert.Equal("factory", factory.Name);
    }

    [Fact]
    public void FromKeyedServices_should_resolve_keyed_constructor_dependency()
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IKeyedContract, AlphaKeyedContract>("alpha");
        services.AddTransient<IKeyedConsumer, KeyedConsumer>();

        using var provider = BuildProvider(services);
        var consumer = provider.GetRequiredService<IKeyedConsumer>();

        Assert.Equal("alpha", consumer.Name);
    }

    [Fact]
    public void Property_injection_should_be_module_scoped()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ModuleAssemblyCollection(new[] { typeof(PropertyTarget).Assembly }));
        services.AddTransient<InjectedDependency>();
        services.AddTransient<PropertyTarget>();

        using var provider = BuildProvider(services);
        var target = provider.GetRequiredService<PropertyTarget>();

        Assert.NotNull(target.Dependency);
    }

    [Fact]
    public void Property_injection_should_not_run_when_class_has_disable_attribute()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ModuleAssemblyCollection(new[] { typeof(DisabledPropertyTarget).Assembly }));
        services.AddTransient<InjectedDependency>();
        services.AddTransient<DisabledPropertyTarget>();

        using var provider = BuildProvider(services);
        var target = provider.GetRequiredService<DisabledPropertyTarget>();

        Assert.Null(target.Dependency);
    }

    [Fact]
    public void OnRegistered_interceptors_should_wrap_service_calls()
    {
        TrackingInterceptor.CallCount = 0;

        var services = new ServiceCollection();
        services.AddSingleton(new ModuleAssemblyCollection(new[] { typeof(EchoService).Assembly }));
        services.OnServiceRegistered(context =>
        {
            if (context.ImplementationType == typeof(EchoService))
            {
                context.Interceptors.Add(typeof(TrackingInterceptor));
            }
        });

        services.AddTransient<TrackingInterceptor>();
        services.AddTransient<IEchoService, EchoService>();

        using var provider = BuildProvider(services);
        var service = provider.GetRequiredService<IEchoService>();
        var result = service.Echo("ok");

        Assert.Equal("ok", result);
        Assert.True(TrackingInterceptor.CallCount > 0);
    }

    private static AutofacServiceProvider BuildProvider(IServiceCollection services)
    {
        var factory = new AutofacServiceProviderFactory();
        var builder = factory.CreateBuilder(services);
        return (AutofacServiceProvider)factory.CreateServiceProvider(builder);
    }

    public interface IKeyedContract
    {
        string Name { get; }
    }

    public sealed class AlphaKeyedContract : IKeyedContract
    {
        public string Name => "alpha";
    }

    public sealed class FactoryKeyedContract : IKeyedContract
    {
        public string Name => "factory";
    }

    public interface IKeyedConsumer
    {
        string Name { get; }
    }

    public sealed class KeyedConsumer : IKeyedConsumer
    {
        public string Name { get; }

        public KeyedConsumer([FromKeyedServices("alpha")] IKeyedContract contract)
        {
            Name = contract.Name;
        }
    }

    public sealed class InjectedDependency
    {
    }

    public sealed class PropertyTarget
    {
        public InjectedDependency? Dependency { get; set; }
    }

    [DisablePropertyInjection]
    public sealed class DisabledPropertyTarget
    {
        [DisablePropertyInjection]
        public InjectedDependency? Dependency { get; set; }
    }

    public interface IEchoService
    {
        string Echo(string value);
    }

    public sealed class EchoService : IEchoService
    {
        public string Echo(string value)
        {
            return value;
        }
    }

    public sealed class TrackingInterceptor : IInterceptor
    {
        public static int CallCount { get; set; }

        public void Intercept(IInvocation invocation)
        {
            CallCount++;
            invocation.Proceed();
        }
    }
}
