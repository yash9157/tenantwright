namespace Shared.Data.ConnectionStrings;

public class SharedConnectionStringResolverOptions
{
    public const string SectionName = "Shared:Data:ConnectionStringResolver";

    public bool ThrowOnMissing { get; set; } = true;

    public bool AllowHostFallbackForTenant { get; set; }

    /// <summary>
    /// In-process cache lifetime, in seconds, for a resolved tenant's
    /// connection-string/active-state snapshot. Bounds how long an
    /// <c>IsActive=true -&gt; false</c> flip can lag before each service enforces
    /// it. Values below 1 are floored to 1 second so a misconfiguration cannot
    /// disable the cache and hammer the tenant store on every resolution.
    /// </summary>
    public int TenantCacheSeconds { get; set; } = 30;
}
