using Microsoft.Extensions.Configuration;

namespace Shared.Modularity.Context;

public class ApplicationContext
{
    public IServiceProvider ServiceProvider { get; }
    public IConfiguration Configuration { get; }

    public ApplicationContext(IServiceProvider serviceProvider, IConfiguration configuration)
    {
        ServiceProvider = serviceProvider;
        Configuration = configuration;
    }
}
