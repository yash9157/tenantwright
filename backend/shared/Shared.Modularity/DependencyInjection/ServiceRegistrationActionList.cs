namespace Shared.Modularity.DependencyInjection;

public sealed class ServiceRegistrationActionList : List<Action<IOnServiceRegisteredContext>>
{
    public bool IsClassInterceptorsDisabled { get; set; }

    public ClassInterceptorsSelectorList DisabledClassInterceptorsSelectors { get; } = new();
}
