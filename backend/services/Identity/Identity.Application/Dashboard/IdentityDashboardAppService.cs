using Identity.EntityFrameworkCore.EntityFrameworkCore;
using Identity.Application.ApplicationUsers;
using Identity.Application.Contracts.Dashboard;
using Identity.Domain.ApplicationUsers;
using Identity.Domain.Shared.ApplicationUsers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Shared;
using Shared.Caching.Abstractions;
using Shared.Data.Filtering;
using Shared.Domain;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;
using Shared.EntityFrameworkCore;
namespace Identity.Application.Dashboard;

public sealed class IdentityDashboardAppService : IIdentityDashboardAppService, ITransientDependency
{
    private static readonly HybridCacheEntryOptions CacheOptions = new()
    {
        Expiration = TimeSpan.FromSeconds(60)
    };

    private static readonly string[] TenantAdminRoleNames =
    [
        TenantWrightIdentityRoleNames.TenantAdmin.ToUpperInvariant()
    ];

    private static readonly string[] ElevatedRoleNames =
    [
        TenantWrightIdentityRoleNames.HostAdmin.ToUpperInvariant(),
        TenantWrightIdentityRoleNames.TenantAdmin.ToUpperInvariant()
    ];

    private static readonly string[] LoginFailureActions =
    [
        "LoginFailed",
        "LoginInvalidPassword"
    ];

    private static readonly string[] LoginOutcomeActions =
    [
        "LoginSuccess",
        "LoginFailed",
        "LoginInvalidPassword",
        "LoginLockedOut",
        "LoginRiskBlocked",
        "TwoFactorChallenge"
    ];

    private static readonly string[] RecentSecurityActions =
    [
        "ManualLock",
        "ManualUnlock",
        "LogoutSelectedSessionsByAdmin",
        "LogoutAllSessionsByAdmin",
        "TenantDeactivated"
    ];

    // MFA-by-tenant worklist ignores single-user tenants: a 1-user tenant at 0% is
    // noise, not posture.
    private const int MfaMinimumTenantUsers = 3;

    private readonly IDbContextProvider<IdentityDbContextApp> _dbContextProvider;
    private readonly ICurrentTenant _currentTenant;
    private readonly IDataFilter _dataFilter;
    private readonly IHybridCache<AdminIdentityDashboardDto, string> _adminCache;
    private readonly IHybridCache<MfaAdoptionDto, string> _mfaCache;
    private readonly IHybridCache<TenantIdentityDashboardDto, string> _tenantCache;

    public IdentityDashboardAppService(
        IDbContextProvider<IdentityDbContextApp> dbContextProvider, ICurrentTenant currentTenant, IDataFilter dataFilter, IHybridCache<AdminIdentityDashboardDto, string> adminCache, IHybridCache<MfaAdoptionDto, string> mfaCache, IHybridCache<TenantIdentityDashboardDto, string> tenantCache)
    {
        _dbContextProvider = dbContextProvider;
        _currentTenant = currentTenant;
        _dataFilter = dataFilter;
        _adminCache = adminCache;
        _mfaCache = mfaCache;
        _tenantCache = tenantCache;
    }

    public Task<AdminIdentityDashboardDto> GetAdminAsync(int? windowDays = null)
    {
        var resolvedWindowDays = ValidateWindowDays(windowDays);
        var cacheKey = $"identity:dashboard:admin:{resolvedWindowDays}";

        return GetAdminCachedAsync(cacheKey, resolvedWindowDays);
    }

    public Task<MfaAdoptionDto> GetMfaAdoptionAsync(int? top = null)
    {
        var resolvedTop = Math.Clamp(top ?? 10, 1, 100);
        var cacheKey = $"identity:dashboard:mfa-adoption:{resolvedTop}";
        return GetMfaAdoptionCachedAsync(cacheKey, resolvedTop);
    }

    public Task<TenantIdentityDashboardDto> GetTenantAsync(int? windowDays = null)
    {
        var resolvedWindowDays = ValidateWindowDays(windowDays);
        var tenantId = _currentTenant.Id ?? throw new SharedValidationException("Tenant context is required.");
        var cacheKey = $"identity:dashboard:tenant:{tenantId:D}:{resolvedWindowDays}";

        return GetTenantCachedAsync(cacheKey, tenantId, resolvedWindowDays);
    }

    private async Task<MfaAdoptionDto> GetMfaAdoptionCachedAsync(string cacheKey, int top)
    {
        var cached = await _mfaCache.GetOrCreateAsync(
            cacheKey,
            async () =>
            {
                using (_currentTenant.Change(null))
                {
                    return await BuildMfaAdoptionAsync(top);
                }
            },
            () => CacheOptions);

        return cached ?? throw new SharedException("MFA adoption cache returned no payload.");
    }

    private async Task<MfaAdoptionDto> BuildMfaAdoptionAsync(int top)
    {
        using var tenantFilter = _dataFilter.Disable<IMultiTenant>();
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var now = DateTime.UtcNow;
        var perTenant = await BuildUserBaseQuery(unitOfWorkDbContext, tenantId: null)
            .Where(x => x.LifecycleStatus == IdentityUserLifecycleStatus.Active)
            .GroupBy(x => x.TenantId)
            .Select(group => new
            {
                TenantId = group.Key,
                EnabledCount = group.Count(x => x.TwoFactorEnabled),
                TotalCount = group.Count()
            })
            .ToListAsync();

        var platformEnabled = perTenant.Sum(x => x.EnabledCount);
        var platformTotal = perTenant.Sum(x => x.TotalCount);

        var tenants = perTenant
            .Where(x => x.TotalCount >= MfaMinimumTenantUsers)
            .Select(x => new MfaTenantAdoptionDto
            {
                TenantId = x.TenantId,
                EnabledCount = x.EnabledCount,
                TotalCount = x.TotalCount,
                AdoptionRate = x.TotalCount == 0 ? 0 : decimal.Round((decimal)x.EnabledCount / x.TotalCount, 4)
            })
            // Weakest posture first: this is a worklist, top row is the first call to make.
            .OrderBy(x => x.AdoptionRate)
            .ThenByDescending(x => x.TotalCount)
            .Take(top)
            .ToList();

        return new MfaAdoptionDto
        {
            GeneratedAtUtc = now,
            PlatformEnabledCount = platformEnabled,
            PlatformTotalCount = platformTotal,
            PlatformAdoptionRate = platformTotal == 0 ? 0 : decimal.Round((decimal)platformEnabled / platformTotal, 4),
            Tenants = tenants
        };
    }

    private async Task<AdminIdentityDashboardDto> GetAdminCachedAsync(string cacheKey, int resolvedWindowDays)
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

        return cached ?? throw new SharedException("Admin identity dashboard cache returned no payload.");
    }

    private async Task<TenantIdentityDashboardDto> GetTenantCachedAsync(string cacheKey, Guid tenantId, int resolvedWindowDays)
    {
        var cached = await _tenantCache.GetOrCreateAsync(
            cacheKey,
            () => BuildTenantAsync(tenantId, resolvedWindowDays),
            () => CacheOptions);

        return cached ?? throw new SharedException("Tenant identity dashboard cache returned no payload.");
    }

    private async Task<AdminIdentityDashboardDto> BuildAdminAsync(int windowDays)
    {
        using var tenantFilter = _dataFilter.Disable<IMultiTenant>();
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var now = DateTime.UtcNow;
        var windowStartUtc = now.Date.AddDays(-(windowDays - 1));
        var last24HoursUtc = now.AddHours(-24);
        var next72HoursUtc = now.AddHours(72);

        var tenantScopedUsers = BuildUserBaseQuery(unitOfWorkDbContext, tenantId: null);
        var tenantAdminUserIds = BuildRoleAssignmentQuery(unitOfWorkDbContext, tenantId: null)
            .Where(x => TenantAdminRoleNames.Contains(x.RoleNormalizedName))
            .Select(x => x.UserId)
            .Distinct();
        var elevatedUserIds = BuildRoleAssignmentQuery(unitOfWorkDbContext, tenantId: null)
            .Where(x => ElevatedRoleNames.Contains(x.RoleNormalizedName))
            .Select(x => x.UserId)
            .Distinct();

        var expiringInvitations = await BuildInvitationBaseQuery(unitOfWorkDbContext, tenantId: null, now)
            .Where(x => x.ExpiresAtUtc <= next72HoursUtc)
            .OrderBy(x => x.ExpiresAtUtc)
            .Take(10)
            .Select(x => new DashboardExpiringInvitationDto
            {
                InvitationId = x.Id,
                TenantId = x.TenantId,
                TenantName = x.TenantName,
                Email = x.Email,
                RoleName = x.RoleName,
                ExpiresAtUtc = x.ExpiresAtUtc
            })
            .ToListAsync();

        var recentSecurityActions = await BuildSecurityLogBaseQuery(unitOfWorkDbContext, tenantId: null)
            .Where(x => x.CreationTime >= last24HoursUtc && RecentSecurityActions.Contains(x.Action))
            .OrderByDescending(x => x.CreationTime)
            .Take(10)
            .Select(x => new DashboardRecentSecurityActionDto
            {
                OccurredAtUtc = x.CreationTime,
                TenantId = x.TenantId,
                UserId = x.UserId,
                UserName = x.UserName,
                Action = x.Action ?? string.Empty,
                IpAddress = x.ClientIpAddress,
                SessionId = x.SessionId
            })
            .ToListAsync();

        var loginOutcomeEntries = await BuildSecurityLogBaseQuery(unitOfWorkDbContext, tenantId: null)
            .Where(x => x.CreationTime >= windowStartUtc && LoginOutcomeActions.Contains(x.Action))
            .Select(x => new SecurityActionPoint
            {
                Action = x.Action ?? string.Empty,
                OccurredAtUtc = x.CreationTime
            })
            .ToListAsync();

        var invitationLifecycleEntries = await BuildInvitationLifecycleEntriesAsync(tenantId: null, windowStartUtc, now);
        var revokedSessionEntries = await BuildSessionBaseQuery(unitOfWorkDbContext, tenantId: null)
            .Where(x => x.Status == IdentitySessionStatus.Revoked && x.RevokedAtUtc.HasValue && x.RevokedAtUtc.Value >= windowStartUtc)
            .Select(x => x.RevokedAtUtc!.Value)
            .ToListAsync();
        var atRiskLogEntries = await BuildAtRiskLogEntriesAsync(tenantId: null, windowStartUtc);

        return new AdminIdentityDashboardDto
        {
            GeneratedAtUtc = now,
            WindowDays = windowDays,
            TotalTenantAdmins = await tenantScopedUsers.Where(x => tenantAdminUserIds.Contains(x.Id)).CountAsync(),
            TotalTenantUsers = await tenantScopedUsers.Where(x => !elevatedUserIds.Contains(x.Id)).CountAsync(),
            InactiveUsers = await tenantScopedUsers
                .Where(x => !x.IsActive || x.LifecycleStatus != IdentityUserLifecycleStatus.Active)
                .CountAsync(),
            LockedUsers = await tenantScopedUsers.Where(x => x.LockoutEnd.HasValue && x.LockoutEnd.Value > now).CountAsync(),
            PendingActivationUsers = await tenantScopedUsers.Where(x => x.RequireEmailActivationBeforeLogin && !x.EmailConfirmed).CountAsync(),
            ActiveSessions = await BuildSessionBaseQuery(unitOfWorkDbContext, tenantId: null).Where(x => x.Status == IdentitySessionStatus.Active).CountAsync(),
            RevokedSessionsLast24h = await BuildSessionBaseQuery(unitOfWorkDbContext, tenantId: null)
                .Where(x => x.Status == IdentitySessionStatus.Revoked && x.RevokedAtUtc.HasValue && x.RevokedAtUtc.Value >= last24HoursUtc)
                .CountAsync(),
            PendingInvitations = await BuildInvitationBaseQuery(unitOfWorkDbContext, tenantId: null, now).CountAsync(),
            ExpiringInvitationsNext72h = await BuildInvitationBaseQuery(unitOfWorkDbContext, tenantId: null, now)
                .Where(x => x.ExpiresAtUtc <= next72HoursUtc)
                .CountAsync(),
            LoginFailuresLast24h = await BuildSecurityLogBaseQuery(unitOfWorkDbContext, tenantId: null)
                .Where(x => x.CreationTime >= last24HoursUtc && LoginFailureActions.Contains(x.Action))
                .CountAsync(),
            LoginLockedOutLast24h = await BuildSecurityLogBaseQuery(unitOfWorkDbContext, tenantId: null)
                .Where(x => x.CreationTime >= last24HoursUtc && x.Action == "LoginLockedOut")
                .CountAsync(),
            RiskBlockedLast24h = await BuildSecurityLogBaseQuery(unitOfWorkDbContext, tenantId: null)
                .Where(x => x.CreationTime >= last24HoursUtc && x.Action == "LoginRiskBlocked")
                .CountAsync(),
            TwoFactorChallengesLast24h = await BuildSecurityLogBaseQuery(unitOfWorkDbContext, tenantId: null)
                .Where(x => x.CreationTime >= last24HoursUtc && x.Action == "TwoFactorChallenge")
                .CountAsync(),
            LoginOutcomeTrend = BuildLoginOutcomeTrend(windowStartUtc, windowDays, loginOutcomeEntries),
            SessionRevocationTrend = BuildSessionTrend(windowStartUtc, windowDays, revokedSessionEntries),
            InvitationLifecycleTrend = BuildInvitationTrend(windowStartUtc, windowDays, invitationLifecycleEntries),
            ExpiringInvitations = expiringInvitations,
            RecentSecurityActions = recentSecurityActions,
            AtRiskUsers = await BuildAtRiskUsersAsync(tenantId: null, atRiskLogEntries)
        };
    }

    private async Task<TenantIdentityDashboardDto> BuildTenantAsync(Guid tenantId, int windowDays)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var now = DateTime.UtcNow;
        var windowStartUtc = now.Date.AddDays(-(windowDays - 1));
        var last24HoursUtc = now.AddHours(-24);
        var next72HoursUtc = now.AddHours(72);

        var tenantUsers = BuildUserBaseQuery(unitOfWorkDbContext, tenantId);
        var tenantAdminUserIds = BuildRoleAssignmentQuery(unitOfWorkDbContext, tenantId)
            .Where(x => TenantAdminRoleNames.Contains(x.RoleNormalizedName))
            .Select(x => x.UserId)
            .Distinct();

        var expiringInvitations = await BuildInvitationBaseQuery(unitOfWorkDbContext, tenantId, now)
            .Where(x => x.ExpiresAtUtc <= next72HoursUtc)
            .OrderBy(x => x.ExpiresAtUtc)
            .Take(10)
            .Select(x => new DashboardExpiringInvitationDto
            {
                InvitationId = x.Id,
                TenantId = x.TenantId,
                TenantName = x.TenantName,
                Email = x.Email,
                RoleName = x.RoleName,
                ExpiresAtUtc = x.ExpiresAtUtc
            })
            .ToListAsync();

        var recentSecurityActions = await BuildSecurityLogBaseQuery(unitOfWorkDbContext, tenantId)
            .Where(x => x.CreationTime >= last24HoursUtc && RecentSecurityActions.Contains(x.Action))
            .OrderByDescending(x => x.CreationTime)
            .Take(10)
            .Select(x => new DashboardRecentSecurityActionDto
            {
                OccurredAtUtc = x.CreationTime,
                TenantId = x.TenantId,
                UserId = x.UserId,
                UserName = x.UserName,
                Action = x.Action ?? string.Empty,
                IpAddress = x.ClientIpAddress,
                SessionId = x.SessionId
            })
            .ToListAsync();

        var loginOutcomeEntries = await BuildSecurityLogBaseQuery(unitOfWorkDbContext, tenantId)
            .Where(x => x.CreationTime >= windowStartUtc && LoginOutcomeActions.Contains(x.Action))
            .Select(x => new SecurityActionPoint
            {
                Action = x.Action ?? string.Empty,
                OccurredAtUtc = x.CreationTime
            })
            .ToListAsync();

        var invitationLifecycleEntries = await BuildInvitationLifecycleEntriesAsync(tenantId, windowStartUtc, now);
        var revokedSessionEntries = await BuildSessionBaseQuery(unitOfWorkDbContext, tenantId)
            .Where(x => x.Status == IdentitySessionStatus.Revoked && x.RevokedAtUtc.HasValue && x.RevokedAtUtc.Value >= windowStartUtc)
            .Select(x => x.RevokedAtUtc!.Value)
            .ToListAsync();

        var atRiskLogEntries = await BuildAtRiskLogEntriesAsync(tenantId, windowStartUtc);

        return new TenantIdentityDashboardDto
        {
            GeneratedAtUtc = now,
            WindowDays = windowDays,
            TenantId = tenantId,
            TenantAdminCount = await tenantUsers.Where(x => tenantAdminUserIds.Contains(x.Id)).CountAsync(),
            TenantUserCount = await tenantUsers.Where(x => !tenantAdminUserIds.Contains(x.Id)).CountAsync(),
            InactiveUsers = await tenantUsers
                .Where(x => !x.IsActive || x.LifecycleStatus != IdentityUserLifecycleStatus.Active)
                .CountAsync(),
            LockedUsers = await tenantUsers.Where(x => x.LockoutEnd.HasValue && x.LockoutEnd.Value > now).CountAsync(),
            PendingActivationUsers = await tenantUsers.Where(x => x.RequireEmailActivationBeforeLogin && !x.EmailConfirmed).CountAsync(),
            ActiveSessions = await BuildSessionBaseQuery(unitOfWorkDbContext, tenantId).Where(x => x.Status == IdentitySessionStatus.Active).CountAsync(),
            RevokedSessionsLast24h = await BuildSessionBaseQuery(unitOfWorkDbContext, tenantId)
                .Where(x => x.Status == IdentitySessionStatus.Revoked && x.RevokedAtUtc.HasValue && x.RevokedAtUtc.Value >= last24HoursUtc)
                .CountAsync(),
            PendingInvitations = await BuildInvitationBaseQuery(unitOfWorkDbContext, tenantId, now).CountAsync(),
            ExpiringInvitationsNext72h = await BuildInvitationBaseQuery(unitOfWorkDbContext, tenantId, now)
                .Where(x => x.ExpiresAtUtc <= next72HoursUtc)
                .CountAsync(),
            LoginFailuresLast24h = await BuildSecurityLogBaseQuery(unitOfWorkDbContext, tenantId)
                .Where(x => x.CreationTime >= last24HoursUtc && LoginFailureActions.Contains(x.Action))
                .CountAsync(),
            LoginLockedOutLast24h = await BuildSecurityLogBaseQuery(unitOfWorkDbContext, tenantId)
                .Where(x => x.CreationTime >= last24HoursUtc && x.Action == "LoginLockedOut")
                .CountAsync(),
            RiskBlockedLast24h = await BuildSecurityLogBaseQuery(unitOfWorkDbContext, tenantId)
                .Where(x => x.CreationTime >= last24HoursUtc && x.Action == "LoginRiskBlocked")
                .CountAsync(),
            TwoFactorChallengesLast24h = await BuildSecurityLogBaseQuery(unitOfWorkDbContext, tenantId)
                .Where(x => x.CreationTime >= last24HoursUtc && x.Action == "TwoFactorChallenge")
                .CountAsync(),
            LoginOutcomeTrend = BuildLoginOutcomeTrend(windowStartUtc, windowDays, loginOutcomeEntries),
            SessionRevocationTrend = BuildSessionTrend(windowStartUtc, windowDays, revokedSessionEntries),
            InvitationLifecycleTrend = BuildInvitationTrend(windowStartUtc, windowDays, invitationLifecycleEntries),
            ExpiringInvitations = expiringInvitations,
            RecentSecurityActions = recentSecurityActions,
            AtRiskUsers = await BuildAtRiskUsersAsync(tenantId, atRiskLogEntries)
        };
    }

    private static IQueryable<ApplicationUser> BuildUserBaseQuery(IdentityDbContextApp unitOfWorkDbContext, Guid? tenantId)
    {
        var query = unitOfWorkDbContext.Users.AsNoTracking().AsQueryable();
        if (tenantId.HasValue)
        {
            return query.Where(x => x.TenantId == tenantId.Value);
        }

        return query.Where(x => x.TenantId != Guid.Empty);
    }

    private static IQueryable<RoleAssignmentPoint> BuildRoleAssignmentQuery(IdentityDbContextApp unitOfWorkDbContext, Guid? tenantId)
    {
        var query =
            from userRole in unitOfWorkDbContext.UserRoles.AsNoTracking()
            join role in unitOfWorkDbContext.Roles.AsNoTracking() on userRole.RoleId equals role.Id
            join user in unitOfWorkDbContext.Users.AsNoTracking() on userRole.UserId equals user.Id
            select new RoleAssignmentPoint
            {
                UserId = userRole.UserId,
                UserTenantId = user.TenantId,
                RoleNormalizedName = role.NormalizedName ?? string.Empty
            };

        if (tenantId.HasValue)
        {
            return query.Where(x => x.UserTenantId == tenantId.Value);
        }

        return query.Where(x => x.UserTenantId.HasValue && x.UserTenantId.Value != Guid.Empty);
    }

    private static IQueryable<IdentitySession> BuildSessionBaseQuery(IdentityDbContextApp unitOfWorkDbContext, Guid? tenantId)
    {
        var query = unitOfWorkDbContext.IdentitySessions.AsNoTracking().AsQueryable();
        if (tenantId.HasValue)
        {
            return query.Where(x => x.TenantId == tenantId.Value);
        }

        return query.Where(x => x.TenantId != null);
    }

    private static IQueryable<IdentityPublicInvitation> BuildInvitationBaseQuery(IdentityDbContextApp unitOfWorkDbContext, Guid? tenantId, DateTime nowUtc)
    {
        var invitations = tenantId.HasValue
            ? unitOfWorkDbContext.IdentityPublicInvitations.AsQueryable()
            : unitOfWorkDbContext.IdentityPublicInvitations.IgnoreQueryFilters();
        var query = invitations.AsNoTracking()
            .Where(x => x.AcceptedAtUtc == null && x.CanceledAtUtc == null && x.ExpiresAtUtc > nowUtc);

        if (tenantId.HasValue)
        {
            return query.Where(x => x.TenantId == tenantId.Value);
        }

        return query;
    }

    private static IQueryable<IdentitySecurityLog> BuildSecurityLogBaseQuery(IdentityDbContextApp unitOfWorkDbContext, Guid? tenantId)
    {
        var query = unitOfWorkDbContext.IdentitySecurityLogs.AsNoTracking().AsQueryable();
        if (tenantId.HasValue)
        {
            return query.Where(x => x.TenantId == tenantId.Value);
        }

        return query.Where(x => x.TenantId != null);
    }

    private async Task<List<InvitationLifecyclePoint>> BuildInvitationLifecycleEntriesAsync(Guid? tenantId, DateTime windowStartUtc, DateTime nowUtc)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var query = (tenantId.HasValue
                ? unitOfWorkDbContext.IdentityPublicInvitations.AsQueryable()
                : unitOfWorkDbContext.IdentityPublicInvitations.IgnoreQueryFilters())
            .AsNoTracking();
        if (tenantId.HasValue)
        {
            query = query.Where(x => x.TenantId == tenantId.Value);
        }

        return await query
            .Where(x =>
                x.CreatedAtUtc >= windowStartUtc ||
                (x.AcceptedAtUtc.HasValue && x.AcceptedAtUtc.Value >= windowStartUtc) ||
                (x.CanceledAtUtc.HasValue && x.CanceledAtUtc.Value >= windowStartUtc) ||
                (x.AcceptedAtUtc == null && x.CanceledAtUtc == null && x.ExpiresAtUtc >= windowStartUtc && x.ExpiresAtUtc <= nowUtc))
            .Select(x => new InvitationLifecyclePoint
            {
                CreatedAtUtc = x.CreatedAtUtc,
                AcceptedAtUtc = x.AcceptedAtUtc,
                CanceledAtUtc = x.CanceledAtUtc,
                ExpiredAtUtc = x.AcceptedAtUtc == null &&
                               x.CanceledAtUtc == null &&
                               x.ExpiresAtUtc <= nowUtc
                    ? x.ExpiresAtUtc
                    : null
            })
            .ToListAsync();
    }

    private async Task<List<SecurityActionWithUserPoint>> BuildAtRiskLogEntriesAsync(Guid? tenantId, DateTime windowStartUtc)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        return await BuildSecurityLogBaseQuery(unitOfWorkDbContext, tenantId)
                    .Where(x =>
                        x.UserId.HasValue &&
                        x.CreationTime >= windowStartUtc &&
                        (x.Action == "LoginFailed" || x.Action == "LoginInvalidPassword" || x.Action == "LoginRiskBlocked" || x.Action == "LoginLockedOut"))
                    .Select(x => new SecurityActionWithUserPoint
                    {
                        Action = x.Action ?? string.Empty,
                        UserId = x.UserId!.Value,
                        UserName = x.UserName,
                        OccurredAtUtc = x.CreationTime
                    })
                    .ToListAsync();
    }

    private async Task<List<DashboardAtRiskUserDto>> BuildAtRiskUsersAsync(Guid? tenantId, List<SecurityActionWithUserPoint> points)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        if (points.Count == 0)
        {
            return [];
        }

        var grouped = points
            .GroupBy(x => x.UserId)
            .Select(group => new
            {
                UserId = group.Key,
                UserName = group.Select(x => x.UserName).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty,
                FailedLoginCount = group.Count(x => x.Action == "LoginFailed" || x.Action == "LoginInvalidPassword"),
                RiskBlockedCount = group.Count(x => x.Action == "LoginRiskBlocked"),
                LockedOutCount = group.Count(x => x.Action == "LoginLockedOut"),
                Score = group.Count(x => x.Action == "LoginFailed" || x.Action == "LoginInvalidPassword" || x.Action == "LoginRiskBlocked")
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.RiskBlockedCount)
            .ThenBy(x => x.UserName)
            .Take(5)
            .ToList();

        var userIds = grouped.Select(x => x.UserId).Distinct().ToList();
        var identityLookup = await BuildUserBaseQuery(unitOfWorkDbContext, tenantId)
            .Where(x => userIds.Contains(x.Id))
            .Select(x => new { x.Id, x.Email, x.TenantId })
            .ToDictionaryAsync(x => x.Id, x => new { x.Email, x.TenantId });

        return grouped.Select(x =>
        {
            var identity = identityLookup.GetValueOrDefault(x.UserId);
            return new DashboardAtRiskUserDto
            {
                UserId = x.UserId,
                TenantId = identity?.TenantId ?? Guid.Empty,
                UserName = x.UserName,
                Email = identity?.Email,
                FailedLoginCount = x.FailedLoginCount,
                RiskBlockedCount = x.RiskBlockedCount,
                LockedOutCount = x.LockedOutCount
            };
        }).ToList();
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

    private static List<DashboardLoginOutcomeTrendDto> BuildLoginOutcomeTrend(DateTime windowStartUtc, int windowDays, List<SecurityActionPoint> points)
    {
        var byDay = points
            .GroupBy(x => x.OccurredAtUtc.Date)
            .ToDictionary(x => x.Key, x => x.ToList());

        var trend = new List<DashboardLoginOutcomeTrendDto>(windowDays);
        for (var offset = 0; offset < windowDays; offset++)
        {
            var dateUtc = windowStartUtc.AddDays(offset).Date;
            byDay.TryGetValue(dateUtc, out var entries);
            entries ??= [];

            trend.Add(new DashboardLoginOutcomeTrendDto
            {
                DateUtc = dateUtc,
                LoginSuccessCount = entries.Count(x => x.Action == "LoginSuccess"),
                LoginFailedCount = entries.Count(x => x.Action == "LoginFailed"),
                LoginInvalidPasswordCount = entries.Count(x => x.Action == "LoginInvalidPassword"),
                LoginLockedOutCount = entries.Count(x => x.Action == "LoginLockedOut"),
                LoginRiskBlockedCount = entries.Count(x => x.Action == "LoginRiskBlocked"),
                TwoFactorChallengeCount = entries.Count(x => x.Action == "TwoFactorChallenge")
            });
        }

        return trend;
    }

    private static List<DashboardSessionTrendDto> BuildSessionTrend(DateTime windowStartUtc, int windowDays, List<DateTime> revokedAtUtc)
    {
        var byDay = revokedAtUtc
            .GroupBy(x => x.Date)
            .ToDictionary(x => x.Key, x => x.Count());

        var trend = new List<DashboardSessionTrendDto>(windowDays);
        for (var offset = 0; offset < windowDays; offset++)
        {
            var dateUtc = windowStartUtc.AddDays(offset).Date;
            trend.Add(new DashboardSessionTrendDto
            {
                DateUtc = dateUtc,
                RevokedCount = byDay.GetValueOrDefault(dateUtc)
            });
        }

        return trend;
    }

    private static List<DashboardInvitationTrendDto> BuildInvitationTrend(DateTime windowStartUtc, int windowDays, List<InvitationLifecyclePoint> points)
    {
        var trend = new List<DashboardInvitationTrendDto>(windowDays);
        for (var offset = 0; offset < windowDays; offset++)
        {
            var dateUtc = windowStartUtc.AddDays(offset).Date;
            var nextDateUtc = dateUtc.AddDays(1);

            trend.Add(new DashboardInvitationTrendDto
            {
                DateUtc = dateUtc,
                CreatedCount = points.Count(x => x.CreatedAtUtc >= dateUtc && x.CreatedAtUtc < nextDateUtc),
                AcceptedCount = points.Count(x => x.AcceptedAtUtc.HasValue && x.AcceptedAtUtc.Value >= dateUtc && x.AcceptedAtUtc.Value < nextDateUtc),
                CanceledCount = points.Count(x => x.CanceledAtUtc.HasValue && x.CanceledAtUtc.Value >= dateUtc && x.CanceledAtUtc.Value < nextDateUtc),
                ExpiredCount = points.Count(x =>
                    x.ExpiredAtUtc.HasValue &&
                    x.ExpiredAtUtc.Value >= dateUtc &&
                    x.ExpiredAtUtc.Value < nextDateUtc)
            });
        }

        return trend;
    }

    private sealed class RoleAssignmentPoint
    {
        public Guid UserId { get; init; }
        public Guid? UserTenantId { get; init; }
        public string RoleNormalizedName { get; init; } = string.Empty;
    }

    private class SecurityActionPoint
    {
        public string Action { get; init; } = string.Empty;
        public DateTime OccurredAtUtc { get; init; }
    }

    private sealed class SecurityActionWithUserPoint : SecurityActionPoint
    {
        public Guid UserId { get; init; }
        public string? UserName { get; init; }
    }

    private sealed class InvitationLifecyclePoint
    {
        public DateTime CreatedAtUtc { get; init; }
        public DateTime? AcceptedAtUtc { get; init; }
        public DateTime? CanceledAtUtc { get; init; }
        public DateTime? ExpiredAtUtc { get; init; }
    }
}
