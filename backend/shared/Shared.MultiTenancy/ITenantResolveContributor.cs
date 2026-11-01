namespace Shared.MultiTenancy;

public interface ITenantResolveContributor
{
    string Name { get; }

    Task<TenantResolveContributorResult> ResolveAsync(
        TenantResolveRequestContext context,
        SharedTenantResolveOptions options,
        CancellationToken cancellationToken = default);
}
