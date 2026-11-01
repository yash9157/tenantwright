namespace Shared.Caching;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface | AttributeTargets.Struct, Inherited = true, AllowMultiple = false)]
public sealed class IgnoreCacheMultiTenancyAttribute : Attribute
{
}
