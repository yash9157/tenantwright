using Microsoft.Extensions.DependencyInjection;

namespace Shared.Modularity.DependencyInjection;

public sealed class ServiceActivatedActionList : List<KeyValuePair<ServiceDescriptor, Action<IOnServiceActivatedContext>>>
{
    public List<Action<IOnServiceActivatedContext>> GetActions(ServiceDescriptor descriptor)
    {
        return this
            .Where(x => x.Key == descriptor)
            .Select(x => x.Value)
            .ToList();
    }
}
