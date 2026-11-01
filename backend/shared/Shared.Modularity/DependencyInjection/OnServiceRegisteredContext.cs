namespace Shared.Modularity.DependencyInjection;

public class OnServiceRegisteredContext : IOnServiceRegisteredContext
{
    public List<Type> Interceptors { get; } = new();

    public Type ServiceType { get; }

    public Type ImplementationType { get; }

    public object? ServiceKey { get; }

    public OnServiceRegisteredContext(Type serviceType, Type implementationType, object? serviceKey = null)
    {
        ServiceType = Check.NotNull(serviceType, nameof(serviceType));
        ImplementationType = Check.NotNull(implementationType, nameof(implementationType));
        ServiceKey = serviceKey;
    }
}
