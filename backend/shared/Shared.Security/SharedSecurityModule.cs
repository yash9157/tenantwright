using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shared.Modularity.Context;
using Shared.Modularity.Modules;

namespace Shared.Security;

public class SharedSecurityModule : BaseModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddHttpContextAccessor();
    }

    public override void OnApplicationInitialization(ApplicationInitializationContext context)
    {
        var logger = context.ServiceProvider.GetService<ILogger<SharedSecurityModule>>();
        if (logger == null)
        {
            return;
        }

        var accessor = context.ServiceProvider.GetService<ICurrentPrincipalAccessor>();
        logger.LogInformation(
            "Shared.Security startup summary: PrincipalAccessorType={PrincipalAccessorType}, SupportsScopedChange={SupportsScopedChange}.",
            accessor?.GetType().FullName ?? "<none>",
            accessor != null);
    }
}
