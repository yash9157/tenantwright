namespace Shared.Caching.Abstractions;

public interface ICacheTenantResolver
{
    Guid? GetTenantIdOrNull();
}

public sealed class NullCacheTenantResolver : ICacheTenantResolver
{
    public Guid? GetTenantIdOrNull()
    {
        return null;
    }
}
