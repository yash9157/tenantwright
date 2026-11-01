namespace Shared.Modularity.DependencyInjection;

public sealed class NamedTypeSelector
{
    public string Name { get; }

    public Func<Type, bool> Predicate { get; }

    public NamedTypeSelector(string name, Func<Type, bool> predicate)
    {
        Name = Check.NotNullOrWhiteSpace(name, nameof(name));
        Predicate = Check.NotNull(predicate, nameof(predicate));
    }
}
