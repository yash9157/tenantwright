using Shared.Modularity.DependencyInjection;

namespace Shared.MultiTenancy;

public abstract class TenantResolveContributorBase : ITenantResolveContributor, ITransientDependency
{
    public abstract string Name { get; }

    public abstract Task<TenantResolveContributorResult> ResolveAsync(
        TenantResolveRequestContext context,
        SharedTenantResolveOptions options,
        CancellationToken cancellationToken = default);

    protected static TenantResolveContributorResult Invalid(string reason)
    {
        return new TenantResolveContributorResult(TenantResolveStatus.InvalidInput, null, reason);
    }

    protected static TenantResolveContributorResult Ambiguous(string reason)
    {
        return new TenantResolveContributorResult(TenantResolveStatus.AmbiguousInput, null, reason);
    }

    protected static TenantResolveContributorResult Resolved(Guid tenantId)
    {
        return new TenantResolveContributorResult(TenantResolveStatus.Resolved, tenantId, null);
    }

    protected static TenantResolveContributorResult Unresolved()
    {
        return new TenantResolveContributorResult(TenantResolveStatus.Unresolved, null, null);
    }
}
