namespace Tenant.Application.Contracts.Dashboard;

using Tenant.Domain.Shared.Enums;

/// <summary>
/// One bucket of the tenant-growth trend. Buckets are contiguous and zero-filled
/// server-side: <see cref="BucketStartUtc"/> is inclusive, <see cref="BucketEndUtc"/>
/// exclusive. <see cref="CreatedCount"/> is the tenants created inside the bucket;
/// <see cref="CumulativeTotal"/> is the running total of all (non-deleted) tenants up
/// to and including the bucket, so the trailing point equals TotalTenants.
/// </summary>
public class DashboardTenantCreationTrendDto
{
    public DateTime BucketStartUtc { get; set; }
    public DateTime BucketEndUtc { get; set; }
    public int CreatedCount { get; set; }
    public int CumulativeTotal { get; set; }
}

public class DashboardRecentTenantDto
{
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public bool IsActive { get; set; }
    public TenantLifecycleStatus LifecycleStatus { get; set; }
}

public class DashboardLifecycleDistributionDto
{
    public TenantLifecycleStatus LifecycleStatus { get; set; }
    public int Count { get; set; }
}

/// <summary>
/// Period-over-period movement for one KPI, measured against the immediately
/// preceding window of equal length. <see cref="PreviousValue"/> and
/// <see cref="DeltaRate"/> are null when there is no prior baseline to divide by, so
/// the client renders "no comparison yet" rather than a misleading zero or infinity.
/// </summary>
public class DashboardMetricDeltaDto
{
    public int? PreviousValue { get; set; }
    public int Delta { get; set; }
    public double? DeltaRate { get; set; }
}

/// <summary>
/// "Needs attention" counts for the portfolio worklist (N1). Each count maps to a
/// tenant-list drill the operator can act on. All are derived from existing data:
/// stalled initial-owner provisioning and chronic suspensions. Tenants share each
/// service database by default, so a tenant without a dedicated database is not
/// an attention item.
/// </summary>
public class DashboardAttentionDto
{
    public int StalledProvisioning { get; set; }
    public int SuspendedOverThirtyDays { get; set; }
}

/// <summary>
/// Truthfully derivable period-over-period comparisons. Total and new-in-window are
/// computed from tenant creation history; active/inactive are intentionally omitted
/// because reconstructing historical lifecycle state would require replaying the
/// transition log and cannot be done honestly from the current snapshot.
/// </summary>
public class DashboardComparisonDto
{
    public DashboardMetricDeltaDto TotalTenants { get; set; } = new();
    public DashboardMetricDeltaDto NewTenants { get; set; } = new();
}

public class AdminTenantDashboardDto
{
    public int WindowDays { get; set; }
    public int TotalTenants { get; set; }
    public int ActiveTenants { get; set; }
    public int InactiveTenants { get; set; }
    public int NewTenantsInWindow { get; set; }

    // Bucket granularity for TenantCreationTrend: "day" for 7/30-day windows,
    // "week" for the 90-day window (90 daily points is sub-pixel noise). Derived
    // server-side from WindowDays so the client never has to guess bucket width.
    public string Granularity { get; set; } = "day";
    public List<DashboardTenantCreationTrendDto> TenantCreationTrend { get; set; } = [];
    public List<DashboardRecentTenantDto> RecentTenants { get; set; } = [];

    // Additive. GeneratedAtUtc reflects the cache-build time (stable within the 60s
    // cache window). LifecycleDistribution is the effective-status breakdown.
    public DateTime GeneratedAtUtc { get; set; }
    public List<DashboardLifecycleDistributionDto> LifecycleDistribution { get; set; } = [];

    // Period-over-period deltas (U2). Additive; null-safe on the client.
    public DashboardComparisonDto Comparison { get; set; } = new();

    // "Needs attention" worklist counts (N1). Additive.
    public DashboardAttentionDto Attention { get; set; } = new();
}

/// <summary>
/// One stage of the onboarding funnel (C3). Stages are strictly monotonic: each
/// count is a subset of the prior stage. <see cref="StalledCount"/> is the subset
/// that reached this stage, did not advance, and has been waiting past the stall
/// threshold. <see cref="MedianHoursInStage"/> is the median dwell for entries that
/// advanced out of the stage (null for terminal stages or where no timestamp bounds
/// the dwell).
/// </summary>
public class TenantOnboardingStageDto
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int Order { get; set; }
    public int Count { get; set; }
    public int StalledCount { get; set; }
    public double? MedianHoursInStage { get; set; }
}

/// <summary>
/// Cohort onboarding funnel over the reporting window (C3). The cohort is the set of
/// initial-owner provisioning requests created within the window; every stage is a
/// strict subset of the stage before it, so stage-to-stage drop-off is the signal.
/// </summary>
public class TenantOnboardingFunnelDto
{
    public DateTime GeneratedAtUtc { get; set; }
    public int WindowDays { get; set; }
    public int StallThresholdHours { get; set; }
    public List<TenantOnboardingStageDto> Stages { get; set; } = [];
}

public class CurrentTenantDashboardDto
{
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public TenantLifecycleStatus LifecycleStatus { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
