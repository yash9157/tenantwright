using Autofac;
using Autofac.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace Shared.Autofac;

public class AutofacServiceProviderFactory : IServiceProviderFactory<ContainerBuilder>
{
    private readonly Action<ContainerBuilder>? _configurationAction;

    public AutofacServiceProviderFactory(Action<ContainerBuilder>? configurationAction = null)
    {
        _configurationAction = configurationAction;
    }

    public ContainerBuilder CreateBuilder(IServiceCollection services)
    {
        var builder = new ContainerBuilder();
        SharedAutofacRegistration.Populate(builder, services);

        _configurationAction?.Invoke(builder);

        return builder;
    }

    public IServiceProvider CreateServiceProvider(ContainerBuilder containerBuilder)
    {
        return new AutofacServiceProvider(containerBuilder.Build());
    }
}
