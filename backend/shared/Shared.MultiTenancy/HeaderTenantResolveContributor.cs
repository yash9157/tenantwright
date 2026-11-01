using Shared.Modularity.DependencyInjection;

namespace Shared.MultiTenancy;

[ExposeServices(typeof(HeaderTenantResolveContributor))]
public class HeaderTenantResolveContributor : TenantResolveContributorBase
{
    public const string ContributorName = "Header";

    public override string Name => ContributorName;

    public override Task<TenantResolveContributorResult> ResolveAsync(
        TenantResolveRequestContext context,
        SharedTenantResolveOptions options,
        CancellationToken cancellationToken = default)
    {
        if (context.Headers.Count == 0)
        {
            return Task.FromResult(Unresolved());
        }

        var keys = BuildKeys(options.HeaderKeys);
        var values = new List<string>();

        foreach (var key in keys)
        {
            if (!context.Headers.TryGetValue(key, out var headerValues))
            {
                continue;
            }

            foreach (var headerValue in headerValues)
            {
                if (string.IsNullOrWhiteSpace(headerValue))
                {
                    continue;
                }

                values.Add(headerValue.Trim());
            }
        }

        if (values.Count == 0)
        {
            return Task.FromResult(Unresolved());
        }

        var distinctValues = values
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (distinctValues.Length > 1)
        {
            return Task.FromResult(Ambiguous("HTTP headers contain multiple tenant identifiers."));
        }

        var candidate = distinctValues[0];
        if (!Guid.TryParse(candidate, out var tenantId) || tenantId == Guid.Empty)
        {
            return Task.FromResult(Invalid("HTTP header tenant identifier is invalid."));
        }

        return Task.FromResult(Resolved(tenantId));
    }

    private static string[] BuildKeys(IEnumerable<string> configuredKeys)
    {
        var keys = configuredKeys
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return keys;
    }
}
