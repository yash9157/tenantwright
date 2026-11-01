using Identity.EntityFrameworkCore.EntityFrameworkCore;
using Identity.Application.ApplicationUsers;
using Identity.Application.Contracts.Account.Risk;
using Identity.Application.Contracts.SecurityAnalytics;
using Identity.Domain.ApplicationUsers;
using Identity.Domain.Shared.Account;
using Identity.Domain.Shared.Account.Risk;
using Identity.Domain.Shared.ApplicationUsers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;
using Shared;
using Shared.Caching.Abstractions;
using Shared.Data.Filtering;
using Shared.Domain;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;
using Shared.EntityFrameworkCore;
namespace Identity.Application.SecurityAnalytics;

public sealed class IdentitySecurityAnalyticsAppService : IIdentitySecurityAnalyticsAppService, ITransientDependency
{
    private static readonly HybridCacheEntryOptions CacheOptions = new()
    {
        Expiration = TimeSpan.FromSeconds(60)
    };

    private static readonly string[] FailureActions =
    [
        "LoginFailed",
        "LoginInvalidPassword"
    ];

    private static readonly string[] RiskRelevantActions =
    [
        "LoginFailed",
        "LoginInvalidPassword",
        "LoginLockedOut",
        "LoginRiskBlocked",
        "TwoFactorChallenge"
    ];

    private static readonly string[] FalsePositiveActions =
    [
        "LoginRiskFalsePositiveMarked",
        "LoginRiskFalsePositiveMarkedByUser"
    ];

    private static readonly string[] ElevatedRoleNames =
    [
        TenantWrightIdentityRoleNames.HostAdmin.ToUpperInvariant(),
        TenantWrightIdentityRoleNames.TenantAdmin.ToUpperInvariant()
    ];

    // India-first regional default: bucket "off hours" in IST (UTC+5:30, no DST) and
    // label the axis explicitly. Revisit if regional scope expands.
    private static readonly TimeSpan HeatmapOffset = TimeSpan.FromHours(5.5);
    private const string HeatmapTimeZone = "Asia/Kolkata";

    private readonly IDbContextProvider<IdentityDbContextApp> _dbContextProvider;
    private readonly ICurrentTenant _currentTenant;
    private readonly IDataFilter _dataFilter;
    private readonly IHybridCache<AdminSecurityAnalyticsDto, string> _adminCache;
    private readonly IHybridCache<SecurityTimingHeatmapDto, string> _heatmapCache;
    private readonly IHybridCache<TenantSecurityAnalyticsDto, string> _tenantCache;
    private readonly IOptions<LoginRiskOptions> _loginRiskOptions;

    public IdentitySecurityAnalyticsAppService(
        IDbContextProvider<IdentityDbContextApp> dbContextProvider, ICurrentTenant currentTenant, IDataFilter dataFilter, IHybridCache<AdminSecurityAnalyticsDto, string> adminCache, IHybridCache<SecurityTimingHeatmapDto, string> heatmapCache, IHybridCache<TenantSecurityAnalyticsDto, string> tenantCache, IOptions<LoginRiskOptions>? loginRiskOptions = null)
    {
        _dbContextProvider = dbContextProvider;
        _currentTenant = currentTenant;
        _dataFilter = dataFilter;
        _adminCache = adminCache;
        _heatmapCache = heatmapCache;
        _tenantCache = tenantCache;
        _loginRiskOptions = loginRiskOptions ?? Options.Create(new LoginRiskOptions());
    }

    private static readonly string[] HeatmapSuccessActions = ["LoginSuccess"];
    private static readonly string[] HeatmapFailureActions = ["LoginFailed", "LoginInvalidPassword", "LoginLockedOut"];

    public Task<AdminSecurityAnalyticsDto> GetAdminAsync(int? windowDays = null)
    {
        var resolvedWindowDays = ValidateWindowDays(windowDays);
        var cacheKey = $"identity:security-analytics:admin:{resolvedWindowDays}";

        return GetAdminCachedAsync(cacheKey, resolvedWindowDays);
    }

    public Task<SecurityTimingHeatmapDto> GetAdminTimingHeatmapAsync(int? windowDays = null, string? outcome = null)
    {
        var resolvedWindowDays = ValidateWindowDays(windowDays);
        var resolvedOutcome = NormalizeOutcome(outcome);
        var cacheKey = $"identity:security-analytics:heatmap:{resolvedWindowDays}:{resolvedOutcome}";

        return GetHeatmapCachedAsync(cacheKey, resolvedWindowDays, resolvedOutcome);
    }

    public Task<TenantSecurityAnalyticsDto> GetTenantAsync(int? windowDays = null)
    {
        var resolvedWindowDays = ValidateWindowDays(windowDays);
        var tenantId = _currentTenant.Id ?? throw new SharedValidationException("Tenant context is required.");
        var cacheKey = $"identity:security-analytics:tenant:{tenantId:D}:{resolvedWindowDays}";

        return GetTenantCachedAsync(cacheKey, tenantId, resolvedWindowDays);
    }

    private async Task<SecurityTimingHeatmapDto> GetHeatmapCachedAsync(string cacheKey, int windowDays, string outcome)
    {
        var cached = await _heatmapCache.GetOrCreateAsync(
            cacheKey,
            async () =>
            {
                using (_currentTenant.Change(null))
                {
                    return await BuildTimingHeatmapAsync(windowDays, outcome);
                }
            },
            () => CacheOptions);

        return cached ?? throw new SharedException("Sign-in timing heatmap cache returned no payload.");
    }

    private async Task<SecurityTimingHeatmapDto> BuildTimingHeatmapAsync(int windowDays, string outcome)
    {
        using var tenantFilter = _dataFilter.Disable<IMultiTenant>();
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var now = DateTime.UtcNow;
        var windowStartUtc = now.Date.AddDays(-(windowDays - 1));
        var query = BuildSecurityLogBaseQuery(unitOfWorkDbContext, tenantId: null, includeHostScope: true)
            .Where(x => x.CreationTime >= windowStartUtc);
        query = outcome switch
        {
            "failure" => query.Where(x => x.Action != null && HeatmapFailureActions.Contains(x.Action)),
            "riskBlocked" => query.Where(x => x.Action == "LoginRiskBlocked" || x.BlockedByRisk == true),
            _ => query.Where(x => x.Action != null && HeatmapSuccessActions.Contains(x.Action)),
        };

        var timestamps = await query.Select(x => x.CreationTime).ToListAsync();

        // Fold into IST-local (day-of-week, hour) buckets in memory (the offset shift is
        // not EF-translatable), then emit the full zero-filled 168-cell grid.
        var counts = new int[7, 24];
        foreach (var utc in timestamps)
        {
            var local = DateTime.SpecifyKind(utc, DateTimeKind.Utc).Add(HeatmapOffset);
            counts[(int)local.DayOfWeek, local.Hour]++;
        }

        var cells = new List<SecurityTimingCellDto>(7 * 24);
        for (var day = 0; day < 7; day++)
        {
            for (var hour = 0; hour < 24; hour++)
            {
                cells.Add(new SecurityTimingCellDto { DayOfWeek = day, Hour = hour, Count = counts[day, hour] });
            }
        }

        return new SecurityTimingHeatmapDto
        {
            GeneratedAtUtc = now,
            WindowDays = windowDays,
            Outcome = outcome,
            TimeZone = HeatmapTimeZone,
            Cells = cells
        };
    }

    private static string NormalizeOutcome(string? outcome)
    {
        return outcome switch
        {
            "failure" => "failure",
            "riskBlocked" => "riskBlocked",
            "success" or null or "" => "success",
            _ => throw new SharedValidationException("outcome must be one of: success, failure, or riskBlocked.")
        };
    }

    private async Task<AdminSecurityAnalyticsDto> GetAdminCachedAsync(string cacheKey, int windowDays)
    {
        var cached = await _adminCache.GetOrCreateAsync(
            cacheKey,
            async () =>
            {
                using (_currentTenant.Change(null))
                {
                    return await BuildAdminAsync(windowDays);
                }
            },
            () => CacheOptions);

        return cached ?? throw new SharedException("Admin security analytics cache returned no payload.");
    }

    private async Task<TenantSecurityAnalyticsDto> GetTenantCachedAsync(string cacheKey, Guid tenantId, int windowDays)
    {
        var cached = await _tenantCache.GetOrCreateAsync(
            cacheKey,
            () => BuildTenantAsync(tenantId, windowDays),
            () => CacheOptions);

        return cached ?? throw new SharedException("Tenant security analytics cache returned no payload.");
    }

    private async Task<AdminSecurityAnalyticsDto> BuildAdminAsync(int windowDays)
    {
        using var tenantFilter = _dataFilter.Disable<IMultiTenant>();
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var now = DateTime.UtcNow;
        var windowStartUtc = now.Date.AddDays(-(windowDays - 1));
        var last24HoursUtc = now.AddHours(-24);
        var next72HoursUtc = now.AddHours(72);

        var tenantScopedLogs = BuildSecurityLogBaseQuery(unitOfWorkDbContext, tenantId: null, includeHostScope: false);
        var currentTenantOverrides = BuildActiveOverrideQuery(unitOfWorkDbContext, tenantId: null, now, excludedUserIds: null);

        var trend = await BuildTrendAsync(
            BuildRiskUserLogQuery(unitOfWorkDbContext, tenantId: null, excludedUserIds: null),
            BuildAdministrativeActionLogQuery(unitOfWorkDbContext, tenantId: null),
            windowStartUtc,
            windowDays);

        return new AdminSecurityAnalyticsDto
        {
            Summary = new SecurityAnalyticsSummaryDto
            {
                GeneratedAtUtc = now,
                WindowDays = windowDays,
                LoginFailuresLast24h = await BuildRiskUserLogQuery(unitOfWorkDbContext, tenantId: null, excludedUserIds: null)
                    .Where(x => x.CreationTime >= last24HoursUtc && x.Action != null && FailureActions.Contains(x.Action))
                    .CountAsync(),
                LoginLockedOutLast24h = await BuildRiskUserLogQuery(unitOfWorkDbContext, tenantId: null, excludedUserIds: null)
                    .Where(x => x.CreationTime >= last24HoursUtc && x.Action == "LoginLockedOut")
                    .CountAsync(),
                RiskBlockedLast24h = await BuildRiskUserLogQuery(unitOfWorkDbContext, tenantId: null, excludedUserIds: null)
                    .Where(x => x.CreationTime >= last24HoursUtc && (x.Action == "LoginRiskBlocked" || x.BlockedByRisk == true))
                    .CountAsync(),
                TwoFactorChallengesLast24h = await BuildRiskUserLogQuery(unitOfWorkDbContext, tenantId: null, excludedUserIds: null)
                    .Where(x => x.CreationTime >= last24HoursUtc && x.Action == "TwoFactorChallenge")
                    .CountAsync(),
                FalsePositivesMarkedLast24h = await BuildAdministrativeActionLogQuery(unitOfWorkDbContext, tenantId: null)
                    .Where(x => x.CreationTime >= last24HoursUtc && x.Action != null && FalsePositiveActions.Contains(x.Action))
                    .CountAsync(),
                ActiveOverrides = await currentTenantOverrides.CountAsync(),
                ExpiringOverridesNext72h = await currentTenantOverrides
                    .Where(x => x.ExpiresAtUtc.HasValue && x.ExpiresAtUtc.Value <= next72HoursUtc)
                    .CountAsync()
            },
            RiskDistribution = await BuildRiskDistributionAsync(now, windowStartUtc),
            DailyTrend = trend,
            TopAtRiskUsers = await BuildTopUsersAsync(tenantId: null, excludedUserIds: null, windowStartUtc, 5),
            TopSourceIps = await BuildTopIpsAsync(tenantId: null, excludedUserIds: null, windowStartUtc, 5),
            ExpiringOverrides = await BuildExpiringOverridesAsync(tenantId: null, excludedUserIds: null, now, next72HoursUtc, 10)
        };
    }

    private async Task<TenantSecurityAnalyticsDto> BuildTenantAsync(Guid tenantId, int windowDays)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var now = DateTime.UtcNow;
        var windowStartUtc = now.Date.AddDays(-(windowDays - 1));
        var last24HoursUtc = now.AddHours(-24);
        var next72HoursUtc = now.AddHours(72);
        var elevatedUserIds = await BuildElevatedUserIdsQuery(unitOfWorkDbContext, tenantId).ToListAsync();

        var riskUserLogs = BuildRiskUserLogQuery(unitOfWorkDbContext, tenantId, elevatedUserIds);
        var administrativeLogs = BuildAdministrativeActionLogQuery(unitOfWorkDbContext, tenantId);
        var activeOverrides = BuildActiveOverrideQuery(unitOfWorkDbContext, tenantId, now, elevatedUserIds);
        var trend = await BuildTrendAsync(riskUserLogs, administrativeLogs, windowStartUtc, windowDays);

        return new TenantSecurityAnalyticsDto
        {
            TenantId = tenantId,
            Summary = new SecurityAnalyticsSummaryDto
            {
                GeneratedAtUtc = now,
                WindowDays = windowDays,
                LoginFailuresLast24h = await riskUserLogs
                    .Where(x => x.CreationTime >= last24HoursUtc && x.Action != null && FailureActions.Contains(x.Action))
                    .CountAsync(),
                LoginLockedOutLast24h = await riskUserLogs
                    .Where(x => x.CreationTime >= last24HoursUtc && x.Action == "LoginLockedOut")
                    .CountAsync(),
                RiskBlockedLast24h = await riskUserLogs
                    .Where(x => x.CreationTime >= last24HoursUtc && (x.Action == "LoginRiskBlocked" || x.BlockedByRisk == true))
                    .CountAsync(),
                TwoFactorChallengesLast24h = await riskUserLogs
                    .Where(x => x.CreationTime >= last24HoursUtc && x.Action == "TwoFactorChallenge")
                    .CountAsync(),
                FalsePositivesMarkedLast24h = await administrativeLogs
                    .Where(x => x.CreationTime >= last24HoursUtc && x.Action != null && FalsePositiveActions.Contains(x.Action))
                    .CountAsync(),
                ActiveOverrides = await activeOverrides.CountAsync(),
                ExpiringOverridesNext72h = await activeOverrides
                    .Where(x => x.ExpiresAtUtc.HasValue && x.ExpiresAtUtc.Value <= next72HoursUtc)
                    .CountAsync()
            },
            DailyTrend = trend,
            TopAtRiskUsers = await BuildTopUsersAsync(tenantId, elevatedUserIds, windowStartUtc, 5),
            TopSourceIps = await BuildTopIpsAsync(tenantId, elevatedUserIds, windowStartUtc, 5),
            ExpiringOverrides = await BuildExpiringOverridesAsync(tenantId, elevatedUserIds, now, next72HoursUtc, 10)
        };
    }

    private static IQueryable<IdentitySecurityLog> BuildSecurityLogBaseQuery(IdentityDbContextApp unitOfWorkDbContext, Guid? tenantId, bool includeHostScope)
    {
        var query = unitOfWorkDbContext.IdentitySecurityLogs.AsNoTracking().AsQueryable();
        if (tenantId.HasValue)
        {
            return query.Where(x => x.TenantId == tenantId.Value);
        }

        return includeHostScope
            ? query
            : query.Where(x => x.TenantId != null);
    }

    private static IQueryable<IdentitySecurityLog> BuildRiskUserLogQuery(IdentityDbContextApp unitOfWorkDbContext, Guid? tenantId, IReadOnlyCollection<Guid>? excludedUserIds)
    {
        var query = BuildSecurityLogBaseQuery(unitOfWorkDbContext, tenantId, includeHostScope: false)
            .Where(x =>
                x.UserId.HasValue &&
                x.Action != null &&
                RiskRelevantActions.Contains(x.Action));

        if (excludedUserIds is { Count: > 0 })
        {
            query = query.Where(x => x.UserId.HasValue && !excludedUserIds.Contains(x.UserId.Value));
        }

        return query;
    }

    private static IQueryable<IdentitySecurityLog> BuildAdministrativeActionLogQuery(IdentityDbContextApp unitOfWorkDbContext, Guid? tenantId)
    {
        return BuildSecurityLogBaseQuery(unitOfWorkDbContext, tenantId, includeHostScope: false)
            .Where(x =>
                x.Action == "LoginRiskOverrideCreated" ||
                x.Action == "LoginRiskFalsePositiveMarked" ||
                x.Action == "LoginRiskFalsePositiveMarkedByUser");
    }

    private static IQueryable<IdentityLoginRiskOverride> BuildActiveOverrideQuery(IdentityDbContextApp unitOfWorkDbContext, Guid? tenantId, DateTime nowUtc, IReadOnlyCollection<Guid>? excludedUserIds)
    {
        var query = unitOfWorkDbContext.IdentityLoginRiskOverrides
            .AsNoTracking()
            .Where(x => x.IsActive && (!x.ExpiresAtUtc.HasValue || x.ExpiresAtUtc.Value > nowUtc));

        if (tenantId.HasValue)
        {
            query = query.Where(x => x.TenantId == tenantId.Value);
        }
        else
        {
            query = query.Where(x => x.TenantId != null);
        }

        if (excludedUserIds is { Count: > 0 })
        {
            query = query.Where(x => !excludedUserIds.Contains(x.UserId));
        }

        return query;
    }

    private static async Task<List<SecurityAnalyticsTrendBucketDto>> BuildTrendAsync(
        IQueryable<IdentitySecurityLog> riskUserLogs,
        IQueryable<IdentitySecurityLog> administrativeLogs,
        DateTime windowStartUtc,
        int windowDays)
    {
        var riskBuckets = await riskUserLogs
            .Where(x => x.CreationTime >= windowStartUtc)
            .GroupBy(x => x.CreationTime.Date)
            .Select(group => new RiskTrendBucket
            {
                DateUtc = group.Key,
                LoginFailedCount = group.Count(x => x.Action == "LoginFailed"),
                LoginInvalidPasswordCount = group.Count(x => x.Action == "LoginInvalidPassword"),
                LoginLockedOutCount = group.Count(x => x.Action == "LoginLockedOut"),
                LoginRiskBlockedCount = group.Count(x => x.Action == "LoginRiskBlocked" || x.BlockedByRisk == true),
                TwoFactorChallengeCount = group.Count(x => x.Action == "TwoFactorChallenge")
            })
            .ToListAsync();

        var administrativeBuckets = await administrativeLogs
            .Where(x => x.CreationTime >= windowStartUtc)
            .GroupBy(x => x.CreationTime.Date)
            .Select(group => new AdministrativeTrendBucket
            {
                DateUtc = group.Key,
                FalsePositiveMarkedCount = group.Count(x => x.Action == "LoginRiskFalsePositiveMarked" || x.Action == "LoginRiskFalsePositiveMarkedByUser"),
                OverrideCreatedCount = group.Count(x => x.Action == "LoginRiskOverrideCreated")
            })
            .ToListAsync();

        var riskLookup = riskBuckets.ToDictionary(x => x.DateUtc, x => x);
        var administrativeLookup = administrativeBuckets.ToDictionary(x => x.DateUtc, x => x);
        var result = new List<SecurityAnalyticsTrendBucketDto>(windowDays);

        for (var offset = 0; offset < windowDays; offset++)
        {
            var dateUtc = windowStartUtc.AddDays(offset).Date;
            riskLookup.TryGetValue(dateUtc, out var riskBucket);
            administrativeLookup.TryGetValue(dateUtc, out var administrativeBucket);

            result.Add(new SecurityAnalyticsTrendBucketDto
            {
                DateUtc = dateUtc,
                LoginFailedCount = riskBucket?.LoginFailedCount ?? 0,
                LoginInvalidPasswordCount = riskBucket?.LoginInvalidPasswordCount ?? 0,
                LoginLockedOutCount = riskBucket?.LoginLockedOutCount ?? 0,
                LoginRiskBlockedCount = riskBucket?.LoginRiskBlockedCount ?? 0,
                TwoFactorChallengeCount = riskBucket?.TwoFactorChallengeCount ?? 0,
                FalsePositiveMarkedCount = administrativeBucket?.FalsePositiveMarkedCount ?? 0,
                OverrideCreatedCount = administrativeBucket?.OverrideCreatedCount ?? 0
            });
        }

        return result;
    }

    private async Task<List<SecurityAnalyticsTopUserDto>> BuildTopUsersAsync(
        Guid? tenantId,
        IReadOnlyCollection<Guid>? excludedUserIds,
        DateTime windowStartUtc,
        int take)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var grouped = await BuildRiskUserLogQuery(unitOfWorkDbContext, tenantId, excludedUserIds)
                    .Where(x => x.CreationTime >= windowStartUtc && x.UserId.HasValue)
                    .GroupBy(x => new { UserId = x.UserId!.Value, x.UserName })
                    .Select(group => new TopUserAggregate
                    {
                        UserId = group.Key.UserId,
                        UserName = group.Key.UserName ?? string.Empty,
                        RiskBlockedCount = group.Count(x => x.Action == "LoginRiskBlocked" || x.BlockedByRisk == true),
                        FailureCount = group.Count(x => x.Action == "LoginFailed" || x.Action == "LoginInvalidPassword"),
                        LockedOutCount = group.Count(x => x.Action == "LoginLockedOut"),
                        LastSeenAtUtc = group.Max(x => x.CreationTime)
                    })
                    .Where(x => x.RiskBlockedCount > 0 || x.FailureCount > 0 || x.LockedOutCount > 0)
                    .OrderByDescending(x => x.RiskBlockedCount)
                    .ThenByDescending(x => x.FailureCount)
                    .ThenByDescending(x => x.LockedOutCount)
                    .ThenByDescending(x => x.LastSeenAtUtc)
                    .ThenBy(x => x.UserName)
                    .Take(take)
                    .ToListAsync();

        if (grouped.Count == 0)
        {
            return [];
        }

        var emailLookup = await unitOfWorkDbContext.Users
            .AsNoTracking()
            .Where(x => grouped.Select(item => item.UserId).Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Email);

        return grouped
            .Select(x => new SecurityAnalyticsTopUserDto
            {
                UserId = x.UserId,
                UserName = x.UserName,
                Email = emailLookup.GetValueOrDefault(x.UserId),
                RiskBlockedCount = x.RiskBlockedCount,
                FailureCount = x.FailureCount,
                LockedOutCount = x.LockedOutCount,
                LastSeenAtUtc = x.LastSeenAtUtc
            })
            .ToList();
    }

    private async Task<List<SecurityAnalyticsTopIpDto>> BuildTopIpsAsync(
        Guid? tenantId,
        IReadOnlyCollection<Guid>? excludedUserIds,
        DateTime windowStartUtc,
        int take)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        return await BuildRiskUserLogQuery(unitOfWorkDbContext, tenantId, excludedUserIds)
                    .Where(x => x.CreationTime >= windowStartUtc && x.ClientIpAddress != null && x.ClientIpAddress != string.Empty)
                    .GroupBy(x => x.ClientIpAddress!)
                    .Select(group => new SecurityAnalyticsTopIpDto
                    {
                        ClientIpAddress = group.Key,
                        RiskBlockedCount = group.Count(x => x.Action == "LoginRiskBlocked" || x.BlockedByRisk == true),
                        FailureCount = group.Count(x => x.Action == "LoginFailed" || x.Action == "LoginInvalidPassword"),
                        LockedOutCount = group.Count(x => x.Action == "LoginLockedOut"),
                        LastSeenAtUtc = group.Max(x => x.CreationTime)
                    })
                    .Where(x => x.RiskBlockedCount > 0 || x.FailureCount > 0 || x.LockedOutCount > 0)
                    .OrderByDescending(x => x.RiskBlockedCount)
                    .ThenByDescending(x => x.FailureCount)
                    .ThenByDescending(x => x.LockedOutCount)
                    .ThenByDescending(x => x.LastSeenAtUtc)
                    .ThenBy(x => x.ClientIpAddress)
                    .Take(take)
                    .ToListAsync();
    }

    private async Task<List<SecurityAnalyticsExpiringOverrideDto>> BuildExpiringOverridesAsync(
        Guid? tenantId,
        IReadOnlyCollection<Guid>? excludedUserIds,
        DateTime nowUtc,
        DateTime next72HoursUtc,
        int take)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        return await (
                    from overrideEntry in BuildActiveOverrideQuery(unitOfWorkDbContext, tenantId, nowUtc, excludedUserIds)
                    join user in unitOfWorkDbContext.Users.AsNoTracking() on overrideEntry.UserId equals user.Id
                    where overrideEntry.ExpiresAtUtc.HasValue && overrideEntry.ExpiresAtUtc.Value <= next72HoursUtc
                    orderby overrideEntry.ExpiresAtUtc, user.UserName
                    select new SecurityAnalyticsExpiringOverrideDto
                    {
                        OverrideId = overrideEntry.Id,
                        UserId = overrideEntry.UserId,
                        UserName = user.UserName ?? string.Empty,
                        ModeOverride = overrideEntry.ModeOverride.ToString(),
                        ExpiresAtUtc = overrideEntry.ExpiresAtUtc,
                        Reason = overrideEntry.Reason
                    })
                    .Take(take)
                    .ToListAsync();
    }

    private async Task<LoginRiskDistributionDto> BuildRiskDistributionAsync(DateTime nowUtc, DateTime windowStartUtc)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var query = BuildSecurityLogBaseQuery(unitOfWorkDbContext, tenantId: null, includeHostScope: true)
                    .Where(x => x.CreationTime >= windowStartUtc && x.RiskScore.HasValue);

        var totalEventCount = await query.CountAsync();
        var distinctUserCount = await query
            .Where(x => x.UserId.HasValue)
            .Select(x => x.UserId!.Value)
            .Distinct()
            .CountAsync();
        var falsePositiveEventCount = await query.CountAsync(x => x.FalsePositive == true);
        var observeDataSinceUtc = await BuildSecurityLogBaseQuery(unitOfWorkDbContext, tenantId: null, includeHostScope: true)
            .Where(x => x.RiskScore.HasValue)
            .MinAsync(x => (DateTime?)x.CreationTime);

        var scoreGroups = await query
            .GroupBy(x => x.RiskScore!.Value >= 100 ? 10 : x.RiskScore.Value / 10)
            .Select(group => new CountAggregate<int>
            {
                Key = group.Key,
                EventCount = group.Count()
            })
            .OrderBy(x => x.Key)
            .ToListAsync();

        var actionGroups = await query
            .GroupBy(x => x.RiskAction ?? "Unknown")
            .Select(group => new CategoryAggregate
            {
                Category = group.Key,
                EventCount = group.Count(),
                DistinctUserCount = group.Where(x => x.UserId.HasValue).Select(x => x.UserId).Distinct().Count()
            })
            .OrderByDescending(x => x.EventCount)
            .ThenBy(x => x.Category)
            .ToListAsync();

        var levelGroups = await query
            .GroupBy(x => x.RiskLevel ?? "Unknown")
            .Select(group => new CategoryAggregate
            {
                Category = group.Key,
                EventCount = group.Count(),
                DistinctUserCount = group.Where(x => x.UserId.HasValue).Select(x => x.UserId).Distinct().Count()
            })
            .OrderByDescending(x => x.EventCount)
            .ThenBy(x => x.Category)
            .ToListAsync();

        var scopeGroups = await query
            .GroupBy(x => x.TenantId.HasValue)
            .Select(group => new CountAggregate<bool>
            {
                Key = group.Key,
                EventCount = group.Count(),
                DistinctUserCount = group.Where(x => x.UserId.HasValue).Select(x => x.UserId).Distinct().Count()
            })
            .ToListAsync();

        var mfaGroups = await query
            .GroupBy(x => x.MfaEnrolled)
            .Select(group => new CountAggregate<bool?>
            {
                Key = group.Key,
                EventCount = group.Count(),
                DistinctUserCount = group.Where(x => x.UserId.HasValue).Select(x => x.UserId).Distinct().Count()
            })
            .ToListAsync();

        var reasonCombinationGroups = await query
            .GroupBy(x => x.RiskReasonFlags)
            .Select(group => new CountAggregate<int?>
            {
                Key = group.Key,
                EventCount = group.Count(),
                DistinctUserCount = group.Where(x => x.UserId.HasValue).Select(x => x.UserId).Distinct().Count()
            })
            .OrderByDescending(x => x.EventCount)
            .ThenBy(x => x.Key)
            .ToListAsync();

        var blockAction = LoginRiskAction.Block.ToString();
        var forceMfaAction = LoginRiskAction.ForceMfa.ToString();
        var wouldBlockEventCount = await query.CountAsync(x => x.RiskAction == blockAction);
        var wouldForceMfaEventCount = await query.CountAsync(x => x.RiskAction == forceMfaAction);
        var wouldDenyForMissingMfaEventCount = await query.CountAsync(
            x => x.RiskAction == forceMfaAction && x.MfaEnrolled != true);
        var affectedUsersQuery = query.Where(
            x => x.RiskAction == blockAction ||
                 (x.RiskAction == forceMfaAction && x.MfaEnrolled != true));
        var distinctAffectedUserCount = await affectedUsersQuery
            .Where(x => x.UserId.HasValue)
            .Select(x => x.UserId!.Value)
            .Distinct()
            .CountAsync();

        var elevatedRoleNames = ElevatedRoleNames;
        var privilegedUsers = await (
            from userRole in unitOfWorkDbContext.UserRoles.AsNoTracking()
            join role in unitOfWorkDbContext.Roles.AsNoTracking() on userRole.RoleId equals role.Id
            join user in unitOfWorkDbContext.Users.AsNoTracking() on userRole.UserId equals user.Id
            where elevatedRoleNames.Contains(role.NormalizedName ?? string.Empty) &&
                  user.IsActive &&
                  user.LifecycleStatus == IdentityUserLifecycleStatus.Active
            select new PrivilegedUserProjection
            {
                UserId = user.Id,
                MfaEnrolled = user.TwoFactorEnabled,
                PasskeyEnrolled = unitOfWorkDbContext.IdentityPasskeyCredentials.Any(credential =>
                    credential.UserId == user.Id &&
                    ((user.TenantId == Guid.Empty && credential.TenantId == null) ||
                     credential.TenantId == user.TenantId))
            })
            .Distinct()
            .ToListAsync();

        var hostAdminRole = TenantWrightIdentityRoleNames.HostAdmin.ToUpperInvariant();
        var eligibleHostAdminRecipientCount = await (
            from userRole in unitOfWorkDbContext.UserRoles.AsNoTracking()
            join role in unitOfWorkDbContext.Roles.AsNoTracking() on userRole.RoleId equals role.Id
            join user in unitOfWorkDbContext.Users.AsNoTracking() on userRole.UserId equals user.Id
            where role.NormalizedName == hostAdminRole &&
                  user.TenantId == Guid.Empty &&
                  user.IsActive &&
                  user.LifecycleStatus == IdentityUserLifecycleStatus.Active &&
                  user.EmailConfirmed &&
                  user.Email != null &&
                  user.Email != string.Empty
            select user.Id)
            .Distinct()
            .CountAsync();

        var observeDataDays = observeDataSinceUtc.HasValue
            ? Math.Max(0, (nowUtc.Date - observeDataSinceUtc.Value.Date).Days + 1)
            : 0;
        var riskOptions = _loginRiskOptions.Value;
        var privilegedUserCount = privilegedUsers.Select(x => x.UserId).Distinct().Count();
        var privilegedMfaEnrolledCount = privilegedUsers
            .GroupBy(x => x.UserId)
            .Count(group => group.Any(x => x.MfaEnrolled || x.PasskeyEnrolled));
        var readiness = BuildReadiness(
            riskOptions,
            observeDataSinceUtc,
            observeDataDays,
            totalEventCount,
            eligibleHostAdminRecipientCount,
            privilegedUserCount,
            privilegedMfaEnrolledCount);

        var combinations = reasonCombinationGroups
            .Select(group => new LoginRiskReasonCombinationDto
            {
                Flags = group.Key ?? 0,
                Reasons = GetReasonNames(group.Key),
                EventCount = group.EventCount,
                DistinctUserCount = group.DistinctUserCount
            })
            .ToList();

        return new LoginRiskDistributionDto
        {
            TotalEvaluatedEventCount = totalEventCount,
            DistinctUserCount = distinctUserCount,
            FalsePositiveEventCount = falsePositiveEventCount,
            FalsePositiveRate = totalEventCount == 0
                ? 0
                : decimal.Round((decimal)falsePositiveEventCount / totalEventCount, 4),
            MfaSnapshotUnknownEventCount = mfaGroups.Where(x => !x.Key.HasValue).Sum(x => x.EventCount),
            ScoreBuckets = scoreGroups.Select(group => new LoginRiskScoreBucketDto
            {
                MinimumScore = group.Key == 10 ? 100 : group.Key * 10,
                MaximumScore = group.Key == 10 ? 100 : group.Key * 10 + 9,
                EventCount = group.EventCount
            }).ToList(),
            Actions = actionGroups.Select(ToCategoryDto).ToList(),
            Levels = levelGroups.Select(ToCategoryDto).ToList(),
            Scopes = scopeGroups.Select(group => new LoginRiskCategoryCountDto
            {
                Category = group.Key ? "Tenant" : "Host",
                EventCount = group.EventCount,
                DistinctUserCount = group.DistinctUserCount
            }).ToList(),
            MfaEnrollment = mfaGroups.Select(group => new LoginRiskCategoryCountDto
            {
                Category = group.Key switch
                {
                    true => "Enrolled",
                    false => "NotEnrolled",
                    null => "Unknown"
                },
                EventCount = group.EventCount,
                DistinctUserCount = group.DistinctUserCount
            }).ToList(),
            Reasons = BuildReasonCounts(reasonCombinationGroups),
            ReasonCombinations = combinations,
            BlastRadius = new LoginRiskBlastRadiusDto
            {
                WouldBlockEventCount = wouldBlockEventCount,
                WouldForceMfaEventCount = wouldForceMfaEventCount,
                WouldDenyForMissingMfaEventCount = wouldDenyForMissingMfaEventCount,
                TotalDeniedEventCount = wouldBlockEventCount + wouldDenyForMissingMfaEventCount,
                DistinctAffectedUserCount = distinctAffectedUserCount
            },
            Readiness = readiness
        };
    }

    private static LoginRiskEnforcementReadinessDto BuildReadiness(
        LoginRiskOptions options,
        DateTime? observeDataSinceUtc,
        int observeDataDays,
        int evaluatedEventCount,
        int eligibleHostAdminRecipientCount,
        int privilegedUserCount,
        int privilegedMfaEnrolledCount)
    {
        var blockers = new List<string>();
        if (!options.Enabled)
        {
            blockers.Add("Login-risk evaluation is disabled.");
        }

        if (options.Mode != LoginRiskMode.Observe)
        {
            blockers.Add("Readiness must be reviewed from Observe mode before enforcement.");
        }

        if (evaluatedEventCount == 0)
        {
            blockers.Add("No scored Observe events are available.");
        }
        else if (observeDataDays < 7)
        {
            blockers.Add("At least seven calendar days of Observe data are required.");
        }

        if (eligibleHostAdminRecipientCount == 0)
        {
            blockers.Add("No active, email-confirmed HostAdmin operator can receive risk alerts.");
        }

        if (privilegedUserCount == 0)
        {
            blockers.Add("No active privileged users were found for MFA readiness measurement.");
        }
        else if (privilegedMfaEnrolledCount < privilegedUserCount)
        {
            blockers.Add("Every active HostAdmin and TenantAdmin must enroll MFA before enforcement.");
        }

        if (!options.GeoSignalTrusted && options.GeoShiftWeight != 0)
        {
            blockers.Add("The untrusted country signal must have an effective weight of zero.");
        }

        return new LoginRiskEnforcementReadinessDto
        {
            Mode = options.Mode,
            ObserveDataSinceUtc = observeDataSinceUtc,
            ObserveDataDays = observeDataDays,
            HasSufficientObserveData = evaluatedEventCount > 0 && observeDataDays >= 7,
            EligibleHostAdminRecipientCount = eligibleHostAdminRecipientCount,
            PrivilegedUserCount = privilegedUserCount,
            PrivilegedMfaEnrolledCount = privilegedMfaEnrolledCount,
            PrivilegedMfaCoverageRate = privilegedUserCount == 0
                ? 0
                : decimal.Round((decimal)privilegedMfaEnrolledCount / privilegedUserCount, 4),
            GeoSignalTrusted = options.GeoSignalTrusted,
            EffectiveGeoShiftWeight = options.GeoSignalTrusted ? options.GeoShiftWeight : 0,
            CanEnableEnforcement = blockers.Count == 0,
            Blockers = blockers
        };
    }

    private static List<LoginRiskReasonCountDto> BuildReasonCounts(
        IEnumerable<CountAggregate<int?>> combinationGroups)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var group in combinationGroups)
        {
            foreach (var reason in GetReasonNames(group.Key))
            {
                counts[reason] = counts.GetValueOrDefault(reason) + group.EventCount;
            }
        }

        return counts
            .Select(item => new LoginRiskReasonCountDto { Reason = item.Key, EventCount = item.Value })
            .OrderByDescending(item => item.EventCount)
            .ThenBy(item => item.Reason)
            .ToList();
    }

    private static List<string> GetReasonNames(int? rawFlags)
    {
        if (!rawFlags.HasValue)
        {
            return ["unclassified"];
        }

        var flags = (IdentityLoginRiskReasonFlags)rawFlags.Value;
        if (flags == IdentityLoginRiskReasonFlags.None)
        {
            return ["none"];
        }

        var reasons = new List<string>();
        AddReason(flags, IdentityLoginRiskReasonFlags.NewDevice, "new_device", reasons);
        AddReason(flags, IdentityLoginRiskReasonFlags.NewIp, "new_ip", reasons);
        AddReason(flags, IdentityLoginRiskReasonFlags.GeoShift, "geo_shift", reasons);
        AddReason(flags, IdentityLoginRiskReasonFlags.FailureBurst, "failure_burst", reasons);
        AddReason(flags, IdentityLoginRiskReasonFlags.OverrideAllow, "override:allow", reasons);
        AddReason(flags, IdentityLoginRiskReasonFlags.OverrideForceMfa, "override:forcemfa", reasons);
        AddReason(flags, IdentityLoginRiskReasonFlags.OverrideBlock, "override:block", reasons);
        AddReason(flags, IdentityLoginRiskReasonFlags.Unknown, "unknown", reasons);
        return reasons;
    }

    private static void AddReason(
        IdentityLoginRiskReasonFlags flags,
        IdentityLoginRiskReasonFlags candidate,
        string name,
        List<string> reasons)
    {
        if ((flags & candidate) != 0)
        {
            reasons.Add(name);
        }
    }

    private static LoginRiskCategoryCountDto ToCategoryDto(CategoryAggregate aggregate)
    {
        return new LoginRiskCategoryCountDto
        {
            Category = aggregate.Category,
            EventCount = aggregate.EventCount,
            DistinctUserCount = aggregate.DistinctUserCount
        };
    }

    private static IQueryable<Guid> BuildElevatedUserIdsQuery(IdentityDbContextApp unitOfWorkDbContext, Guid tenantId)
    {
        return
            from userRole in unitOfWorkDbContext.UserRoles.AsNoTracking()
            join role in unitOfWorkDbContext.Roles.AsNoTracking() on userRole.RoleId equals role.Id
            join user in unitOfWorkDbContext.Users.AsNoTracking() on userRole.UserId equals user.Id
            where user.TenantId == tenantId && ElevatedRoleNames.Contains(role.NormalizedName ?? string.Empty)
            select userRole.UserId;
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

    private sealed class RiskTrendBucket
    {
        public DateTime DateUtc { get; init; }
        public int LoginFailedCount { get; init; }
        public int LoginInvalidPasswordCount { get; init; }
        public int LoginLockedOutCount { get; init; }
        public int LoginRiskBlockedCount { get; init; }
        public int TwoFactorChallengeCount { get; init; }
    }

    private sealed class AdministrativeTrendBucket
    {
        public DateTime DateUtc { get; init; }
        public int FalsePositiveMarkedCount { get; init; }
        public int OverrideCreatedCount { get; init; }
    }

    private sealed class TopUserAggregate
    {
        public Guid UserId { get; init; }
        public string UserName { get; init; } = string.Empty;
        public int RiskBlockedCount { get; init; }
        public int FailureCount { get; init; }
        public int LockedOutCount { get; init; }
        public DateTime LastSeenAtUtc { get; init; }
    }

    private sealed class CountAggregate<TKey>
    {
        public TKey Key { get; init; } = default!;
        public int EventCount { get; init; }
        public int DistinctUserCount { get; init; }
    }

    private sealed class CategoryAggregate
    {
        public string Category { get; init; } = string.Empty;
        public int EventCount { get; init; }
        public int DistinctUserCount { get; init; }
    }

    private sealed class PrivilegedUserProjection
    {
        public Guid UserId { get; init; }
        public bool MfaEnrolled { get; init; }
        public bool PasskeyEnrolled { get; init; }
    }
}
