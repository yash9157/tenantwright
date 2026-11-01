using Shared.MultiTenancy;

namespace Identity.Application.Tenancy;

internal static class TenantContextExtensions
{
    public static Guid RequireTenantId(this ICurrentTenant currentTenant)
    {
        if (!currentTenant.Id.HasValue || currentTenant.Id.Value == Guid.Empty)
        {
            throw new InvalidOperationException("Tenant context is required.");
        }

        return currentTenant.Id.Value;
    }
}
