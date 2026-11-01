namespace Shared.MultiTenancy;

public interface ITenantResolver
{
    Task<TenantResolveResult> ResolveAsync(TenantResolveRequestContext context, CancellationToken cancellationToken = default);
}
