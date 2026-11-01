using Microsoft.Extensions.Caching.Hybrid;
using Shared;
using Shared.Caching.Abstractions;
using Shared.Domain.Entities;
using Shared.Domain.Repositories;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;
using Tenant.Application.Contracts.Dashboard;
using Tenant.Domain.Tenants;
using Microsoft.EntityFrameworkCore;

namespace Tenant.Application.Dashboard;

public sealed class TenantDashboardAppService : ITenantDashboardAppService, ITransientDependency
{
    private static readonly HybridCacheEntryOptions CacheOptions = new()
    {
        Expiration = TimeSpan.FromSeconds(60)
    };
    private const int StalledProvisioningThresholdHours = 48;
    private const int ChronicSuspensionDays = 30;

    private readonly IRepository<Domain.Tenants.Tenant, Guid> _tenantRepository;
    private readonly IRepository<TenantOwnershipRequest, Guid> _ownershipRepository;
    private readonly IRepository<TenantLifecycleTransition, Guid> _transitionRepository;
    private readonly ICurrentTenant _currentTenant;
    private readonly IHybridCache<AdminTenantDashboardDto, string> _adminCache;
    private readonly IHybridCache<TenantOnboardingFunnelDto, string> _funnelCache;
    private readonly IHybridCache<CurrentTenantDashboardDto, string> _currentCache;

    public TenantDashboardAppService(
        IRepository<Domain.Tenants.Tenant, Guid> tenantRepository,
        IRepository<TenantOwnershipRequest, Guid> ownershipRepository,
        IRepository<TenantLifecycleTransition, Guid> transitionRepository,
        ICurrentTenant currentTenant,
        IHybridCache<AdminTenantDashboardDto, string> adminCache,
        IHybridCache<TenantOnboardingFunnelDto, string> funnelCache,
        IHybridCache<CurrentTenantDashboardDto, string> currentCache)
    {
        _tenantRepository = tenantRepository;
        _ownershipRepository = ownershipRepository;
        _transitionRepository = transitionRepository;
        _currentTenant = currentTenant;
        _adminCache = adminCache;
        _funnelCache = funnelCache;
        _currentCache = currentCache;
    }

    public Task<AdminTenantDashboardDto> GetAdminAsync(int? windowDays = null)
    {
        var resolvedWindowDays = ValidateWindowDays(windowDays);
        var cacheKey = $"tenant:dashboard:admin:{resolvedWindowDays}";

        return GetAdminCachedAsync(cacheKey, resolvedWindowDays);
    }

    public Task<TenantOnboardingFunnelDto> GetOnboardingFunnelAsync(int? windowDays = null, int? stallThresholdHours = null)
    {
        var resolvedWindowDays = ValidateWindowDays(windowDays);
        var resolvedThresholdHours = ValidateStallThresholdHours(stallThresholdHours);
        var cacheKey = $"tenant:dashboard:funnel:{resolvedWindowDays}:{resolvedThresholdHours}";

        return GetFunnelCachedAsync(cacheKey, resolvedWindowDays, resolvedThresholdHours);
    }

    public Task<CurrentTenantDashboardDto> GetCurrentAsync()
    {
        var tenantId = _currentTenant.Id ?? throw new SharedValidationException("Tenant context is required.");
        var cacheKey = $"tenant:dashboard:current:{tenantId:D}";

        return GetCurrentCachedAsync(cacheKey, tenantId);
    }

    private async Task<AdminTenantDashboardDto> GetAdminCachedAsync(string cacheKey, int resolvedWindowDays)
    {
        var cached = await _adminCache.GetOrCreateAsync(
            cacheKey,
            async () =>
            {
                using (_currentTenant.Change(null))
                {
                    return await BuildAdminAsync(resolvedWindowDays);
                }
            },
            () => CacheOptions);

        return cached ?? throw new SharedException("Admin tenant dashboard cache returned no payload.");
    }

    private async Task<TenantOnboardingFunnelDto> GetFunnelCachedAsync(string cacheKey, int windowDays, int stallThresholdHours)
    {
        var cached = await _funnelCache.GetOrCreateAsync(
            cacheKey,
            async () =>
            {
                using (_currentTenant.Change(null))
                {
                    return await BuildFunnelAsync(windowDays, stallThresholdHours);
                }
            },
            () => CacheOptions);

        return cached ?? throw new SharedException("Onboarding funnel cache returned no payload.");
    }

    private async Task<CurrentTenantDashboardDto> GetCurrentCachedAsync(string cacheKey, Guid tenantId)
    {
        var cached = await _currentCache.GetOrCreateAsync(
            cacheKey,
            () => BuildCurrentAsync(tenantId),
            () => CacheOptions);

        return cached ?? throw new SharedException("Current tenant dashboard cache returned no payload.");
    }

    private async Task<AdminTenantDashboardDto> BuildAdminAsync(int windowDays)
    {
        var now = DateTime.UtcNow;
        var windowStartUtc = now.Date.AddDays(-(windowDays - 1));
        var windowEndExclusiveUtc = now.Date.AddDays(1);
        // 90 daily points render as sub-pixel noise, so the widest window rolls up to
        // weekly buckets. 7/30-day windows stay daily.
        var stepDays = windowDays >= 90 ? 7 : 1;
        var granularity = stepDays == 7 ? "week" : "day";
        var tenantQuery = (await _tenantRepository.GetQueryableAsync())
            .AsNoTracking();

        var creationPoints = await tenantQuery
            .Where(x => x.CreationTime >= windowStartUtc)
            .Select(x => x.CreationTime)
            .ToListAsync();

        // Baseline for the cumulative series: every tenant that already existed before
        // the window opened. Cumulative points build on this so the trailing point
        // equals TotalTenants. It also serves as the "total at window start" for the
        // period-over-period comparison.
        var priorTenantCount = await tenantQuery.CountAsync(x => x.CreationTime < windowStartUtc);

        // New tenants in the immediately preceding window of equal length, for the
        // new-in-window delta.
        var priorWindowStartUtc = windowStartUtc.AddDays(-windowDays);
        var newTenantsPreviousWindow = await tenantQuery
            .CountAsync(x => x.CreationTime >= priorWindowStartUtc && x.CreationTime < windowStartUtc);

        var recentTenants = await tenantQuery
            .OrderByDescending(x => x.CreationTime)
            .ThenBy(x => x.Name)
            .Take(10)
            .Select(x => new DashboardRecentTenantDto
            {
                TenantId = x.Id,
                Name = x.Name,
                CreatedAtUtc = x.CreationTime,
                IsActive = x.IsActive,
                LifecycleStatus = x.IsActive ? x.LifecycleStatus : Tenant.Domain.Shared.Enums.TenantLifecycleStatus.Suspended
            })
            .ToListAsync();

        // Group by stored status + active flag in the DB, then fold to effective status
        // in memory (GetEffectiveLifecycleStatus is not EF-translatable).
        var lifecycleGroups = await tenantQuery
            .GroupBy(x => new { x.LifecycleStatus, x.IsActive })
            .Select(g => new { g.Key.LifecycleStatus, g.Key.IsActive, Count = g.Count() })
            .ToListAsync();

        var lifecycleDistribution = lifecycleGroups
            .GroupBy(g => !g.IsActive && g.LifecycleStatus == Tenant.Domain.Shared.Enums.TenantLifecycleStatus.Active
                ? Tenant.Domain.Shared.Enums.TenantLifecycleStatus.Suspended
                : g.LifecycleStatus)
            .Select(g => new DashboardLifecycleDistributionDto { LifecycleStatus = g.Key, Count = g.Sum(x => x.Count) })
            .OrderBy(x => x.LifecycleStatus)
            .ToList();

        return new AdminTenantDashboardDto
        {
            WindowDays = windowDays,
            TotalTenants = await tenantQuery.CountAsync(),
            ActiveTenants = await tenantQuery.CountAsync(x => x.IsActive),
            InactiveTenants = await tenantQuery.CountAsync(x => !x.IsActive),
            NewTenantsInWindow = creationPoints.Count,
            Granularity = granularity,
            TenantCreationTrend = BuildCreationTrend(windowStartUtc, windowEndExclusiveUtc, stepDays, priorTenantCount, creationPoints),
            RecentTenants = recentTenants,
            GeneratedAtUtc = now,
            LifecycleDistribution = lifecycleDistribution,
            Comparison = new DashboardComparisonDto
            {
                // Total at window start is the pre-window baseline; the delta is the net
                // additions during the window.
                TotalTenants = BuildDelta(priorTenantCount + creationPoints.Count, priorTenantCount),
                NewTenants = BuildDelta(creationPoints.Count, newTenantsPreviousWindow)
            },
            Attention = await BuildAttentionAsync(tenantQuery, now)
        };
    }

    private async Task<DashboardAttentionDto> BuildAttentionAsync(IQueryable<Domain.Tenants.Tenant> tenantQuery, DateTime now)
    {
        // Initial-owner provisioning requests still waiting past the stall threshold.
        var stallCutoffUtc = now.AddHours(-StalledProvisioningThresholdHours);
        var stalledProvisioning = await (await _ownershipRepository.GetQueryableAsync())
            .AsNoTracking()
            .CountAsync(x =>
                x.Kind == Domain.Shared.Enums.TenantOwnershipRequestKind.InitialOwner &&
                (x.Status == Domain.Shared.Enums.TenantOwnershipRequestStatus.PendingInvitation ||
                 x.Status == Domain.Shared.Enums.TenantOwnershipRequestStatus.AwaitingEmailVerification) &&
                x.CreationTime <= stallCutoffUtc);

        // Chronic suspensions: currently-inactive tenants whose most recent lifecycle
        // transition (or creation, for legacy rows with no transitions) is older than
        // the chronic threshold.
        var suspendedCutoffUtc = now.AddDays(-ChronicSuspensionDays);
        var inactiveTenants = await tenantQuery
            .Where(x => !x.IsActive)
            .Select(x => new { x.Id, x.CreationTime })
            .ToListAsync();

        var suspendedOverThreshold = 0;
        if (inactiveTenants.Count > 0)
        {
            var inactiveIds = inactiveTenants.Select(x => x.Id).ToList();
            var latestTransitions = (await (await _transitionRepository.GetQueryableAsync())
                    .AsNoTracking()
                    .Where(x => inactiveIds.Contains(x.TenantId))
                    .GroupBy(x => x.TenantId)
                    .Select(g => new { TenantId = g.Key, Latest = g.Max(x => x.CreationTime) })
                    .ToListAsync())
                .ToDictionary(x => x.TenantId, x => x.Latest);

            suspendedOverThreshold = inactiveTenants.Count(t =>
                (latestTransitions.TryGetValue(t.Id, out var enteredAt) ? enteredAt : t.CreationTime) <= suspendedCutoffUtc);
        }

        return new DashboardAttentionDto
        {
            StalledProvisioning = stalledProvisioning,
            SuspendedOverThirtyDays = suspendedOverThreshold
        };
    }

    private static DashboardMetricDeltaDto BuildDelta(int currentValue, int previousValue)
    {
        return new DashboardMetricDeltaDto
        {
            PreviousValue = previousValue,
            Delta = currentValue - previousValue,
            // No prior baseline to divide by -> no rate, rather than a misleading 0 or infinity.
            DeltaRate = previousValue == 0 ? null : (double)(currentValue - previousValue) / previousValue
        };
    }

    private async Task<CurrentTenantDashboardDto> BuildCurrentAsync(Guid tenantId)
    {
        var tenant = await _tenantRepository.FindAsync(x => x.Id == tenantId, includeDetails: false);

        if (tenant == null)
        {
            throw new EntityNotFoundException(typeof(Domain.Tenants.Tenant), tenantId);
        }

        return new CurrentTenantDashboardDto
        {
            TenantId = tenant.Id,
            Name = tenant.Name,
            IsActive = tenant.IsActive,
            LifecycleStatus = tenant.GetEffectiveLifecycleStatus(),
            CreatedAtUtc = tenant.CreationTime
        };
    }

    private static int ValidateWindowDays(int? windowDays)
    {
        var resolvedWindowDays = windowDays ?? 30;
        if (resolvedWindowDays is 7 or 30 or 90)
        {
            return resolvedWindowDays;
        }

        throw new SharedValidationException("windowDays must be one of: 7, 30, or 90.");
    }

    private async Task<TenantOnboardingFunnelDto> BuildFunnelAsync(int windowDays, int stallThresholdHours)
    {
        var now = DateTime.UtcNow;
        var windowStartUtc = now.Date.AddDays(-(windowDays - 1));
        var stallCutoffUtc = now.AddHours(-stallThresholdHours);

        // Cohort = initial-owner provisioning requests created inside the window.
        var cohort = await (await _ownershipRepository.GetQueryableAsync())
            .AsNoTracking()
            .Where(x => x.Kind == Domain.Shared.Enums.TenantOwnershipRequestKind.InitialOwner && x.CreationTime >= windowStartUtc)
            .Select(x => new FunnelRow
            {
                TenantId = x.TenantId,
                CreatedAtUtc = x.CreationTime,
                InvitationCreatedAtUtc = x.InvitationCreatedAtUtc,
                CompletedAtUtc = x.CompletedAtUtc,
                PermissionsProvisionedAtUtc = x.PermissionsProvisionedAtUtc
            })
            .ToListAsync();

        // "Active" is the final stage: permissions provisioned AND the tenant is live.
        var tenantIds = cohort.Select(x => x.TenantId).Distinct().ToList();
        var activeTenantIds = tenantIds.Count == 0
            ? new HashSet<Guid>()
            : (await (await _tenantRepository.GetQueryableAsync())
                .AsNoTracking()
                .Where(t => tenantIds.Contains(t.Id) && t.IsActive)
                .Select(t => t.Id)
                .ToListAsync())
                .ToHashSet();

        bool ReachedInvite(FunnelRow r) => r.InvitationCreatedAtUtc.HasValue;
        bool ReachedVerified(FunnelRow r) => r.CompletedAtUtc.HasValue;
        bool ReachedProvisioned(FunnelRow r) => r.PermissionsProvisionedAtUtc.HasValue;
        bool ReachedActive(FunnelRow r) => r.PermissionsProvisionedAtUtc.HasValue && activeTenantIds.Contains(r.TenantId);

        var stages = new List<TenantOnboardingStageDto>
        {
            new()
            {
                Key = "created", Label = "Created", Order = 1,
                Count = cohort.Count,
                StalledCount = cohort.Count(r => !ReachedInvite(r) && r.CreatedAtUtc <= stallCutoffUtc),
                MedianHoursInStage = MedianHours(cohort.Where(ReachedInvite)
                    .Select(r => (r.InvitationCreatedAtUtc!.Value - r.CreatedAtUtc).TotalHours))
            },
            new()
            {
                Key = "invitationSent", Label = "Invitation sent", Order = 2,
                Count = cohort.Count(ReachedInvite),
                StalledCount = cohort.Count(r => ReachedInvite(r) && !ReachedVerified(r) && r.InvitationCreatedAtUtc!.Value <= stallCutoffUtc),
                MedianHoursInStage = MedianHours(cohort.Where(ReachedVerified)
                    .Select(r => (r.CompletedAtUtc!.Value - r.InvitationCreatedAtUtc!.Value).TotalHours))
            },
            new()
            {
                Key = "ownerVerified", Label = "Owner verified", Order = 3,
                Count = cohort.Count(ReachedVerified),
                StalledCount = cohort.Count(r => ReachedVerified(r) && !ReachedProvisioned(r) && r.CompletedAtUtc!.Value <= stallCutoffUtc),
                MedianHoursInStage = MedianHours(cohort.Where(ReachedProvisioned)
                    .Select(r => (r.PermissionsProvisionedAtUtc!.Value - r.CompletedAtUtc!.Value).TotalHours))
            },
            new()
            {
                Key = "permissionsProvisioned", Label = "TenantAdmin provisioned", Order = 4,
                Count = cohort.Count(ReachedProvisioned),
                StalledCount = cohort.Count(r => ReachedProvisioned(r) && !ReachedActive(r) && r.PermissionsProvisionedAtUtc!.Value <= stallCutoffUtc),
                // No activation timestamp bounds this stage, so dwell is not measured.
                MedianHoursInStage = null
            },
            new()
            {
                Key = "active", Label = "Active", Order = 5,
                Count = cohort.Count(ReachedActive),
                StalledCount = 0,
                MedianHoursInStage = null
            }
        };

        return new TenantOnboardingFunnelDto
        {
            GeneratedAtUtc = now,
            WindowDays = windowDays,
            StallThresholdHours = stallThresholdHours,
            Stages = stages
        };
    }

    private static double? MedianHours(IEnumerable<double> values)
    {
        var ordered = values.Where(v => v >= 0).OrderBy(v => v).ToList();
        if (ordered.Count == 0)
        {
            return null;
        }

        var mid = ordered.Count / 2;
        var median = ordered.Count % 2 == 1 ? ordered[mid] : (ordered[mid - 1] + ordered[mid]) / 2;
        return Math.Round(median, 2);
    }

    private static int ValidateStallThresholdHours(int? stallThresholdHours)
    {
        var resolved = stallThresholdHours ?? 48;
        if (resolved is < 1 or > 8760)
        {
            throw new SharedValidationException("stallThresholdHours must be between 1 and 8760.");
        }

        return resolved;
    }

    private sealed class FunnelRow
    {
        public Guid TenantId { get; init; }
        public DateTime CreatedAtUtc { get; init; }
        public DateTime? InvitationCreatedAtUtc { get; init; }
        public DateTime? CompletedAtUtc { get; init; }
        public DateTime? PermissionsProvisionedAtUtc { get; init; }
    }

    private static List<DashboardTenantCreationTrendDto> BuildCreationTrend(
        DateTime windowStartUtc,
        DateTime windowEndExclusiveUtc,
        int stepDays,
        int priorTenantCount,
        List<DateTime> creationTimes)
    {
        var trend = new List<DashboardTenantCreationTrendDto>();
        var cumulative = priorTenantCount;

        for (var bucketStart = windowStartUtc; bucketStart < windowEndExclusiveUtc; bucketStart = bucketStart.AddDays(stepDays))
        {
            var bucketEnd = bucketStart.AddDays(stepDays);
            if (bucketEnd > windowEndExclusiveUtc)
            {
                bucketEnd = windowEndExclusiveUtc;
            }

            var createdCount = creationTimes.Count(x => x >= bucketStart && x < bucketEnd);
            cumulative += createdCount;

            trend.Add(new DashboardTenantCreationTrendDto
            {
                BucketStartUtc = bucketStart,
                BucketEndUtc = bucketEnd,
                CreatedCount = createdCount,
                CumulativeTotal = cumulative
            });
        }

        return trend;
    }
}
