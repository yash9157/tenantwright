namespace Shared.MultiTenancy;

public class SharedTenantResolveOptions
{
    public const string SectionName = "Shared:MultiTenancy:Resolver";

    public bool FailOnInvalidTenantInput { get; set; } = true;

    public bool FailOnAmbiguousTenantInput { get; set; } = true;

    public List<string> HeaderKeys { get; } =
    [
        "X-Tenant-Id"
    ];

    public List<Type> Resolvers { get; } =
    [
        typeof(CurrentUserTenantResolveContributor),
        typeof(HeaderTenantResolveContributor)
    ];

    public List<string> HostOnlyPathPrefixes { get; } = [];
}
