namespace Tenant.Application.Tenants;

public sealed class TenantCacheOptions
{
    public int MaxAbsoluteExpirationHours { get; set; } = 720;

    /// <summary>
    /// Whether the periodic DB-&gt;Valkey reconcile worker runs. Self-heals a
    /// flushed/cold Valkey cache within <see cref="ReconcileIntervalSeconds"/>
    /// instead of requiring a service restart.
    /// </summary>
    public bool ReconcileEnabled { get; set; } = true;

    /// <summary>
    /// How often the reconcile worker calls <c>TenantCacheManager.RefreshAllAsync</c>.
    /// </summary>
    public int ReconcileIntervalSeconds { get; set; } = 300;
}
