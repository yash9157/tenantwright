using Shared.Modularity.Modules;
using Shared.Security;
using Shared.Modularity.Context;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace Shared.CurrentUser;

[DependsOn(typeof(SharedSecurityModule))]
public class CurrentUserModule : BaseModule
{
    public override void OnApplicationInitialization(ApplicationInitializationContext context)
    {
        var logger = context.ServiceProvider.GetService<ILogger<CurrentUserModule>>();
        if (logger == null)
        {
            return;
        }

        logger.LogInformation(
            "Shared.CurrentUser startup summary: ClaimMappingMode={ClaimMappingMode}.",
            "StrictAliasPrecedenceV1");
    }
}
