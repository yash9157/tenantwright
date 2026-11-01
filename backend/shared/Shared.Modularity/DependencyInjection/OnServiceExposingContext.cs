namespace Shared.Modularity.DependencyInjection;

public sealed class OnServiceExposingContext : IOnServiceExposingContext
{
    public Type ImplementationType { get; }

    public List<ServiceIdentifier> ExposedTypes { get; }

    public OnServiceExposingContext(Type implementationType, List<ServiceIdentifier> exposedTypes)
    {
        ImplementationType = Check.NotNull(implementationType, nameof(implementationType));
        ExposedTypes = Check.NotNull(exposedTypes, nameof(exposedTypes));
    }
}
