using Microsoft.Extensions.DependencyInjection;

namespace Shared.Modularity.DependencyInjection;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class DependencyAttribute : Attribute
{
    public ServiceLifetime Lifetime { get; }
    public bool ReplaceServices { get; set; }
    public bool TryRegister { get; set; }

    public DependencyAttribute(ServiceLifetime lifetime)
    {
        Lifetime = lifetime;
    }
}
