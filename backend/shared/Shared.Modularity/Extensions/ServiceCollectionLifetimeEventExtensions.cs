using Shared.Modularity.DependencyInjection;
using Shared.Modularity.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

public static class ServiceCollectionLifetimeEventExtensions
{
    public static void OnActivated(
        this IServiceCollection services,
        ServiceDescriptor descriptor,
        Action<IOnServiceActivatedContext> onActivatedAction)
    {
        GetOrCreateActivatedActionList(services)
            .Add(new KeyValuePair<ServiceDescriptor, Action<IOnServiceActivatedContext>>(descriptor, onActivatedAction));
    }

    public static ServiceActivatedActionList GetServiceActivatedActionList(this IServiceCollection services)
    {
        return GetOrCreateActivatedActionList(services);
    }

    private static ServiceActivatedActionList GetOrCreateActivatedActionList(IServiceCollection services)
    {
        var list = services.GetSingletonInstanceOrNull<IObjectAccessor<ServiceActivatedActionList>>()?.Value;
        if (list != null)
        {
            return list;
        }

        list = new ServiceActivatedActionList();
        services.AddObjectAccessor(list);
        return list;
    }
}
