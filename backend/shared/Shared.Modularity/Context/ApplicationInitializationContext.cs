using Microsoft.AspNetCore.Builder;

namespace Shared.Modularity.Context;

public class ApplicationInitializationContext
{
    public IServiceProvider ServiceProvider { get; }
    public IApplicationBuilder ApplicationBuilder { get; }

    public ApplicationInitializationContext(IServiceProvider serviceProvider, IApplicationBuilder applicationBuilder)
    {
        ServiceProvider = serviceProvider;
        ApplicationBuilder = applicationBuilder;
    }
}
