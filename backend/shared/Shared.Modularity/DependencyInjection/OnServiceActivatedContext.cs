namespace Shared.Modularity.DependencyInjection;

public sealed class OnServiceActivatedContext : IOnServiceActivatedContext
{
    public object Instance { get; }

    public OnServiceActivatedContext(object instance)
    {
        Instance = Check.NotNull(instance, nameof(instance));
    }
}
