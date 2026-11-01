using Autofac;
using Microsoft.Extensions.Hosting;

namespace Shared.Autofac;

public static class AutofacHostBuilderExtensions
{
    public static IHostBuilder UseSharedAutofac(this IHostBuilder hostBuilder, Action<ContainerBuilder>? configurationAction = null)
    {
        return hostBuilder.UseServiceProviderFactory(new AutofacServiceProviderFactory(configurationAction));
    }
}
