namespace Shared.Modularity.DependencyInjection;

public sealed record LegacyAliasRegistration(Type ImplementationType, Type ServiceType);

public sealed class ConventionalRegistrationReport
{
    private readonly List<LegacyAliasRegistration> _legacyAliases = new();

    public IReadOnlyList<LegacyAliasRegistration> LegacyAliases => _legacyAliases;

    public void AddLegacyAlias(Type implementationType, Type serviceType)
    {
        _legacyAliases.Add(new LegacyAliasRegistration(implementationType, serviceType));
    }
}
