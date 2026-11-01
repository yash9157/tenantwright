namespace Shared.Modularity.DependencyInjection;

public interface IOnServiceRegisteredContext
{
    List<Type> Interceptors { get; }

    Type ServiceType { get; }

    Type ImplementationType { get; }

    object? ServiceKey { get; }
}
