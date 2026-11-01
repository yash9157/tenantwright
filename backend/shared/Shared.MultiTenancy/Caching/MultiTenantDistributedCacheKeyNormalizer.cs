using Shared.Caching.Abstractions;

namespace Shared.MultiTenancy.Caching;

public sealed class CurrentTenantCacheTenantResolver : ICacheTenantResolver
{
    private readonly ICurrentTenant _currentTenant;

    public CurrentTenantCacheTenantResolver(ICurrentTenant currentTenant)
    {
        _currentTenant = currentTenant;
    }

    public Guid? GetTenantIdOrNull()
    {
        return _currentTenant.Id;
    }
}
