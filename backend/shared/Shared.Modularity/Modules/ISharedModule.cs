using System.Threading.Tasks;
using Shared.Modularity.Context;

namespace Shared.Modularity.Modules;

public interface ISharedModule
{
    Task PreConfigureServicesAsync(ServiceConfigurationContext context);
    void PreConfigureServices(ServiceConfigurationContext context);

    Task ConfigureServicesAsync(ServiceConfigurationContext context);
    void ConfigureServices(ServiceConfigurationContext context);

    Task PostConfigureServicesAsync(ServiceConfigurationContext context);
    void PostConfigureServices(ServiceConfigurationContext context);

    Task OnPreApplicationInitializationAsync(ApplicationInitializationContext context);
    void OnPreApplicationInitialization(ApplicationInitializationContext context);

    Task OnApplicationInitializationAsync(ApplicationInitializationContext context);
    void OnApplicationInitialization(ApplicationInitializationContext context);

    Task OnPostApplicationInitializationAsync(ApplicationInitializationContext context);
    void OnPostApplicationInitialization(ApplicationInitializationContext context);

    Task OnApplicationShutdownAsync(ApplicationShutdownContext context);
    void OnApplicationShutdown(ApplicationShutdownContext context);
}
