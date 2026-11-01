using Microsoft.Extensions.DependencyInjection;
using Shared.Modularity.Modules;
using Shared.Modularity.Context;
using Shared.Data;
using Shared.Domain.Repositories;
using Shared.Modularity.DependencyInjection;

namespace Shared.Domain;

[DependsOn(
    typeof(SharedDataModule),
    typeof(SharedDomainSharedModule)
)]
public class SharedDomainModule : BaseModule
{
    public override void PreConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.OnExposing(exposingContext =>
        {
            if (!typeof(IRepository).IsAssignableFrom(exposingContext.ImplementationType))
            {
                return;
            }

            exposingContext.ExposedTypes.RemoveAll(service => !service.ServiceType.IsInterface);
        });
    }

    public override void ConfigureServices(ServiceConfigurationContext context)
    {
    }
}
