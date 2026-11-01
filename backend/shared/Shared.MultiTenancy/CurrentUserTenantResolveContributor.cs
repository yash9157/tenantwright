using Shared.Modularity.DependencyInjection;
using Shared.Security;

namespace Shared.MultiTenancy;

[ExposeServices(typeof(CurrentUserTenantResolveContributor))]
public class CurrentUserTenantResolveContributor : TenantResolveContributorBase
{
    public const string ContributorName = "CurrentUser";

    public override string Name => ContributorName;

    public override Task<TenantResolveContributorResult> ResolveAsync(
        TenantResolveRequestContext context,
        SharedTenantResolveOptions options,
        CancellationToken cancellationToken = default)
    {
        if (!context.IsAuthenticated || context.Claims.Count == 0)
        {
            return Task.FromResult(Unresolved());
        }

        var claimValues = context.Claims
            .Where(claim =>
                claim.Type == SharedClaimTypes.TenantId ||
                claim.Type == SharedClaimTypes.LegacyTenantId)
            .Select(claim => claim.Value?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (claimValues.Length == 0)
        {
            return Task.FromResult(Unresolved());
        }

        if (claimValues.Length > 1)
        {
            return Task.FromResult(Ambiguous("Authenticated user has multiple tenant claims."));
        }

        if (!Guid.TryParse(claimValues[0], out var tenantId) || tenantId == Guid.Empty)
        {
            return Task.FromResult(Invalid("Authenticated user tenant claim is not a valid tenant identifier."));
        }

        return Task.FromResult(Resolved(tenantId));
    }
}
