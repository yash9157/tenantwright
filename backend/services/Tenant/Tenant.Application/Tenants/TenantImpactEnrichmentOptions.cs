namespace Tenant.Application.Tenants;

/// <summary>
/// Bounded, optional dependency reads used only to enrich a host-side impact preview.
/// A missing or unavailable dependency is represented in the response; it never blocks
/// a Tenant management decision.
/// </summary>
public sealed class TenantImpactEnrichmentOptions
{
    public string? IdentityBaseAddress { get; set; }
    public string? NotificationsBaseAddress { get; set; }

    /// <summary>
    /// The <c>OpenIddict:Clients:{profile}</c> section whose client-credentials identity reads the counts. Tenant calls
    /// Identity and Notifications under the shared internal client (<c>auth-internal</c>) both allow-list.
    /// </summary>
    public string ClientProfile { get; set; } = "Internal";

    public int TimeoutMilliseconds { get; set; } = 1500;
}
