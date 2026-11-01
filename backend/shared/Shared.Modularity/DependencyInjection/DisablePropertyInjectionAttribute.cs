namespace Shared.Modularity.DependencyInjection;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property)]
public sealed class DisablePropertyInjectionAttribute : Attribute
{
}
