namespace Notifications.Application.RateLimiting;

/// <summary>
/// Per-tenant, per-channel send quotas enforced by the scheduler before persisting a notification.
/// Bound from <c>Notifications:RateLimit</c>. Disabled by default (opt-in): enable and tune per
/// deployment. Atomic stable-operation reservations cover tenant/channel and global budgets;
/// quotas do not replace authorization.
/// </summary>
public sealed class NotificationsRateLimitOptions
{
    public bool Enabled { get; set; }

    /// <summary>Fixed counting window in seconds.</summary>
    public int WindowSeconds { get; set; } = 60;

    /// <summary>Default maximum messages per (tenant, channel) per window.</summary>
    public int PerTenantChannelPerWindow { get; set; } = 600;

    /// <summary>Extra headroom added on top of the per-(tenant, channel) limit to absorb short bursts.</summary>
    public int BurstAllowance { get; set; }

    /// <summary>Host-wide maximum messages per window across all tenants/channels. 0 disables the global cap.</summary>
    public int GlobalPerWindow { get; set; } = 20000;

    /// <summary>Per-channel overrides of <see cref="PerTenantChannelPerWindow"/>, keyed by channel name (e.g. "Sms").</summary>
    public Dictionary<string, int> PerChannelOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
