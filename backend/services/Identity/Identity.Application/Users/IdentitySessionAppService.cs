using Identity.EntityFrameworkCore.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Identity.Application.ApplicationUsers;
using Identity.Application.Contracts.Users;
using Identity.Domain.ApplicationUsers;
using Identity.Domain.Shared.ApplicationUsers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Shared.Caching.Abstractions;
using Shared.Caching.Sessions;
using Shared.CurrentUser;
using Shared.Domain.Entities;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;

using Shared.AuditLogs.Auditing;
using Shared.EntityFrameworkCore;
namespace Identity.Application.Users;

public partial class IdentitySessionAppService : IIdentitySessionAppService, ITransientDependency
{
    private static readonly TimeSpan NegativeCacheDuration = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan LastActiveUpdateInterval = TimeSpan.FromMinutes(1);
    private static readonly string[] ElevatedTenantRoleNames =
    [
        TenantWrightIdentityRoleNames.HostAdmin.ToUpperInvariant(),
        TenantWrightIdentityRoleNames.TenantAdmin.ToUpperInvariant()
    ];

    private readonly IDbContextProvider<IdentityDbContextApp> _dbContextProvider;
    private readonly ICurrentUser _currentUser;
    private readonly ICurrentTenant _currentTenant;
    private readonly IDistributedCache<IdentitySessionValidityCacheItem> _sessionValidityCache;
    private readonly IIdentitySessionRevocationService _sessionRevocationService;
    private readonly TenantUserManagementGuard _managementGuard;
    private readonly ITenantStore _tenantStore;
    private readonly ISessionMarkerStore _sessionMarkerStore;
    private readonly Microsoft.Extensions.Options.IOptions<Shared.Caching.Sessions.SessionValidationOptions> _sessionValidationOptions;

    public IdentitySessionAppService(
        IDbContextProvider<IdentityDbContextApp> dbContextProvider, ICurrentUser currentUser, IDistributedCache<IdentitySessionValidityCacheItem> sessionValidityCache, ICurrentTenant currentTenant, IIdentitySessionRevocationService sessionRevocationService, TenantUserManagementGuard managementGuard, ITenantStore tenantStore, ISessionMarkerStore sessionMarkerStore, Microsoft.Extensions.Options.IOptions<Shared.Caching.Sessions.SessionValidationOptions> sessionValidationOptions)
    {
        _dbContextProvider = dbContextProvider;
        _currentUser = currentUser;
        _sessionValidityCache = sessionValidityCache;
        _currentTenant = currentTenant;
        _sessionRevocationService = sessionRevocationService;
        _managementGuard = managementGuard;
        _tenantStore = tenantStore;
        _sessionMarkerStore = sessionMarkerStore;
        _sessionValidationOptions = sessionValidationOptions;
    }

    private IQueryable<IdentitySession> ApplyCurrentTenantScope(IQueryable<IdentitySession> query)
    {
        if (_currentTenant.Id.HasValue)
        {
            return query.Where(x => x.TenantId == _currentTenant.Id.Value);
        }

        if (_currentUser.IsAuthenticated)
        {
            return query.Where(x => x.TenantId == null);
        }

        return query;
    }

    private IQueryable<IdentitySession> ApplySessionTenantScope(IQueryable<IdentitySession> query, Guid? tenantId)
    {
        return tenantId.HasValue
            ? query.IgnoreQueryFilters().Where(x => x.TenantId == tenantId.Value)
            : ApplyCurrentTenantScope(query);
    }

    private async Task<int> RevokeAllManagedByUserIdAsync(Guid userId, string? revocationReason)
    {
        var targetUser = await ResolveManagedTargetUserAsync(userId);
        return await _sessionRevocationService.RevokeAllAsync(
            targetUser.Id,
            targetUser.TenantId,
            action: "LogoutAllSessionsByAdmin",
            revocationSource: "ManagedUserRevokeAll",
            revokedByUserId: _currentUser.Id,
            revocationReason: NormalizeManagedBulkRevokeReason(revocationReason));
    }

    private static string NormalizeManagedBulkRevokeReason(string? revocationReason)
    {
        if (string.IsNullOrWhiteSpace(revocationReason))
        {
            return "Administrative session revoke";
        }

        return revocationReason.Trim();
    }

    private async Task<ApplicationUser> ResolveManagedTargetUserAsync(Guid userId)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        if (_managementGuard.IsCurrentUserHostAdmin())
        {
            var user = await unitOfWorkDbContext.Users.FirstOrDefaultAsync(x => x.Id == userId);
            if (user == null)
            {
                throw new UnauthorizedAccessException("Cross-tenant session access is not allowed.");
            }

            // Platform staff are deliberately stored in the host scope using Guid.Empty.
            // A host administrator may manage that explicit platform scope, while a
            // tenant administrator never reaches this branch.
            if (user.TenantId == Guid.Empty)
            {
                return user;
            }

            var targetRoles = TenantWrightIdentityRoleNames.NormalizeMany(await unitOfWorkDbContext.UserRoles
                .Where(x => x.UserId == user.Id)
                .Join(unitOfWorkDbContext.Roles, userRole => userRole.RoleId, role => role.Id, (_, role) => role.Name!)
                .ToListAsync());
            if (targetRoles.Any(TenantWrightIdentityRoleNames.IsHostAdminRole))
            {
                throw new UnauthorizedAccessException("Host admin sessions are outside the management scope.");
            }

            return user;
        }

        if (_managementGuard.IsCurrentUserTenantAdmin())
        {
            var tenantId = _managementGuard.RequireTenantId();
            var user = await unitOfWorkDbContext.Users.FirstOrDefaultAsync(x => x.Id == userId && x.TenantId == tenantId);
            if (user == null)
            {
                throw new UnauthorizedAccessException("Cross-tenant session access is not allowed.");
            }

            await _managementGuard.EnsureCanManageTargetAsync(user);
            return user;
        }

        throw new UnauthorizedAccessException("Managed session access requires admin or tenant-admin scope.");
    }

    private void EnsureSessionManagementActor()
    {
        if (_managementGuard.IsCurrentUserHostAdmin() || _managementGuard.IsCurrentUserTenantAdmin())
        {
            return;
        }

        throw new UnauthorizedAccessException("Managed session operations require admin or tenant-admin scope.");
    }

    private static IQueryable<IdentitySession> ApplyUserSessionFilters(IQueryable<IdentitySession> query, GetUserSessionsPagedInput input)
    {
        if (TryParseStatus(input.Status, out var status) && status.HasValue)
        {
            var parsedStatus = status.Value;
            query = query.Where(x => x.Status == parsedStatus);
        }
        else
        {
            query = query.Where(x => x.Status == IdentitySessionStatus.Active);
        }

        var filter = input.Filter?.Trim().ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(filter))
        {
            query = query.Where(x =>
                x.SessionId.ToUpper().Contains(filter) ||
                (x.DeviceId != null && x.DeviceId.ToUpper().Contains(filter)) ||
                (x.IpAddress != null && x.IpAddress.ToUpper().Contains(filter)) ||
                (x.UserAgent != null && x.UserAgent.ToUpper().Contains(filter)));
        }

        var deviceFilter = input.DeviceFilter?.Trim().ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(deviceFilter))
        {
            query = query.Where(x =>
                x.DeviceId != null && x.DeviceId.ToUpper().Contains(deviceFilter));
        }

        if (input.FromUtc.HasValue)
        {
            query = query.Where(x => x.CreationTime >= input.FromUtc.Value || x.LastActiveTime >= input.FromUtc.Value);
        }

        if (input.ToUtc.HasValue)
        {
            query = query.Where(x => x.CreationTime <= input.ToUtc.Value || x.LastActiveTime <= input.ToUtc.Value);
        }

        if (input.RevokedFromUtc.HasValue)
        {
            query = query.Where(x => x.RevokedAtUtc.HasValue && x.RevokedAtUtc.Value >= input.RevokedFromUtc.Value);
        }

        if (input.RevokedToUtc.HasValue)
        {
            query = query.Where(x => x.RevokedAtUtc.HasValue && x.RevokedAtUtc.Value <= input.RevokedToUtc.Value);
        }

        return query;
    }

    private IQueryable<OperationalSessionQueryRow> BuildOperationalSessionQuery(IdentityDbContextApp unitOfWorkDbContext)
    {
        var query =
            from session in unitOfWorkDbContext.IdentitySessions.AsNoTracking()
            join user in unitOfWorkDbContext.Users.AsNoTracking() on session.UserId equals user.Id
            where (session.TenantId.HasValue && user.TenantId == session.TenantId) ||
                  (!session.TenantId.HasValue && user.TenantId == Guid.Empty)
            select new OperationalSessionQueryRow
            {
                Session = session,
                User = user
            };

        if (_managementGuard.IsCurrentUserHostAdmin())
        {
            return query;
        }

        var tenantId = _managementGuard.RequireTenantId();
        var elevatedUserIds = ElevatedUserIdsQuery(unitOfWorkDbContext, tenantId);
        return query.Where(x => x.Session.TenantId == tenantId && !elevatedUserIds.Contains(x.User.Id));
    }

    private static IQueryable<Guid> ElevatedUserIdsQuery(IdentityDbContextApp unitOfWorkDbContext, Guid tenantId)
    {
        return from userRole in unitOfWorkDbContext.UserRoles
               join role in unitOfWorkDbContext.Roles on userRole.RoleId equals role.Id
               where role.TenantId == tenantId &&
                     role.NormalizedName != null &&
                     ElevatedTenantRoleNames.Contains(role.NormalizedName)
               select userRole.UserId;
    }

    private async Task<IReadOnlyDictionary<Guid, string>> GetTenantNameLookupAsync()
    {
        var tenants = await _tenantStore.GetListAsync();
        return tenants
            .GroupBy(x => x.Id)
            .ToDictionary(x => x.Key, x => x.First().Name, comparer: EqualityComparer<Guid>.Default);
    }

    private static IQueryable<IdentitySession> ApplySorting(IQueryable<IdentitySession> query, string? sorting)
    {
        var normalized = sorting?.Trim();
        return normalized?.ToLowerInvariant() switch
        {
            // Direction-complete: every field declared on GetUserSessionsPagedInput carries both
            // directions, and a bare field name always means ascending. The previous shape gave
            // "creationtime" and "revokedatutc" a descending meaning while "status" and "ipaddress"
            // meant ascending, and had no case at all for "creationtime desc", "lastactivetime" or
            // "revokedatutc desc" — those fell through to the default order.
            "creationtime" or "creationtime asc" => query.OrderBy(x => x.CreationTime).ThenBy(x => x.Id),
            "creationtime desc" => query.OrderByDescending(x => x.CreationTime).ThenByDescending(x => x.Id),
            "lastactivetime" or "lastactivetime asc" => query.OrderBy(x => x.LastActiveTime).ThenBy(x => x.Id),
            "lastactivetime desc" => query.OrderByDescending(x => x.LastActiveTime).ThenByDescending(x => x.Id),
            "status" or "status asc" => query.OrderBy(x => x.Status).ThenByDescending(x => x.LastActiveTime),
            "status desc" => query.OrderByDescending(x => x.Status).ThenByDescending(x => x.LastActiveTime),
            "revokedatutc" or "revokedatutc asc" => query.OrderBy(x => x.RevokedAtUtc).ThenBy(x => x.Id),
            "revokedatutc desc" => query.OrderByDescending(x => x.RevokedAtUtc).ThenByDescending(x => x.Id),
            "ipaddress" or "ipaddress asc" => query.OrderBy(x => x.IpAddress).ThenByDescending(x => x.LastActiveTime),
            "ipaddress desc" => query.OrderByDescending(x => x.IpAddress).ThenByDescending(x => x.LastActiveTime),
            _ => query.OrderByDescending(x => x.LastActiveTime).ThenByDescending(x => x.Id)
        };
    }

    private static IQueryable<OperationalSessionQueryRow> ApplyOperationalSorting(IQueryable<OperationalSessionQueryRow> query, string? sorting)
    {
        var normalized = sorting?.Trim();
        return normalized?.ToLowerInvariant() switch
        {
            // Direction-complete, matching the fields declared on GetOperationalSessionsInput. A bare
            // field name always means ascending.
            "creationtime" or "creationtime asc" => query.OrderBy(x => x.Session.CreationTime).ThenBy(x => x.Session.Id),
            "creationtime desc" => query.OrderByDescending(x => x.Session.CreationTime).ThenByDescending(x => x.Session.Id),
            "lastactivetime" or "lastactivetime asc" => query.OrderBy(x => x.Session.LastActiveTime).ThenBy(x => x.Session.Id),
            "lastactivetime desc" => query.OrderByDescending(x => x.Session.LastActiveTime).ThenByDescending(x => x.Session.Id),
            "status" or "status asc" => query.OrderBy(x => x.Session.Status).ThenByDescending(x => x.Session.LastActiveTime),
            "status desc" => query.OrderByDescending(x => x.Session.Status).ThenByDescending(x => x.Session.LastActiveTime),
            // Ordered by TenantId because the operational row carries the tenant identifier, not its
            // display name; the declared field keeps the caller-facing name stable.
            "tenantname" or "tenantname asc" => query.OrderBy(x => x.Session.TenantId).ThenByDescending(x => x.Session.LastActiveTime),
            "tenantname desc" => query.OrderByDescending(x => x.Session.TenantId).ThenByDescending(x => x.Session.LastActiveTime),
            "username" or "username asc" => query.OrderBy(x => x.User.UserName).ThenByDescending(x => x.Session.LastActiveTime),
            "username desc" => query.OrderByDescending(x => x.User.UserName).ThenByDescending(x => x.Session.LastActiveTime),
            "email" or "email asc" => query.OrderBy(x => x.User.Email).ThenByDescending(x => x.Session.LastActiveTime),
            "email desc" => query.OrderByDescending(x => x.User.Email).ThenByDescending(x => x.Session.LastActiveTime),
            "revokedatutc" or "revokedatutc asc" => query.OrderBy(x => x.Session.RevokedAtUtc).ThenBy(x => x.Session.Id),
            "revokedatutc desc" => query.OrderByDescending(x => x.Session.RevokedAtUtc).ThenByDescending(x => x.Session.Id),
            _ => query.OrderByDescending(x => x.Session.LastActiveTime).ThenByDescending(x => x.Session.Id)
        };
    }

    private static bool TryParseStatus(string? status, out IdentitySessionStatus? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(status))
        {
            return false;
        }

        if (!Enum.TryParse<IdentitySessionStatus>(status.Trim(), ignoreCase: true, out var value))
        {
            return false;
        }

        parsed = value;
        return true;
    }

    private async Task<BulkRevokeSessionsResultDto> RevokeKnownSessionsAsync(
        List<IdentitySession> sessions,
        string action,
        string revocationSource)
    {
        if (sessions.Count == 0)
        {
            return new BulkRevokeSessionsResultDto();
        }

        return await _sessionRevocationService.RevokeSelectedAsync(
            sessions.Select(x => x.Id).Distinct().ToList(),
            action,
            revocationSource,
            revokedByUserId: _currentUser.Id);
    }

    private IdentitySessionDto MapToDto(IdentitySession session)
    {
        return new IdentitySessionDto
        {
            Id = session.Id,
            SessionId = session.SessionId,
            DeviceId = session.DeviceId,
            IpAddress = session.IpAddress,
            UserAgent = session.UserAgent,
            DeviceLabel = BuildDeviceLabel(session.DeviceId, session.UserAgent),
            IsCurrentSession = IsCurrentSession(session),
            UserId = session.UserId,
            CreationTime = session.CreationTime,
            LastActiveTime = session.LastActiveTime,
            Status = session.Status.ToString(),
            RevokedAtUtc = session.RevokedAtUtc,
            RevokedByUserId = session.RevokedByUserId,
            RevocationSource = session.RevocationSource,
            RevocationReason = session.RevocationReason
        };
    }

    private bool IsCurrentSession(IdentitySession session)
    {
        var currentSessionId = _currentUser.SessionId;
        if (string.IsNullOrWhiteSpace(currentSessionId))
        {
            return false;
        }

        var currentSessionGuid = TryParseSessionId(currentSessionId);
        return currentSessionGuid == session.Id ||
               string.Equals(session.SessionId, currentSessionId, StringComparison.Ordinal);
    }

    private static string BuildDeviceLabel(string? deviceId, string? userAgent)
    {
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            return deviceId.Trim();
        }

        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            return userAgent.Trim();
        }

        return "Unrecognized device";
    }

    private static OperationalSessionDto MapToOperationalDto(
        OperationalSessionQueryRow row,
        IReadOnlyDictionary<Guid, string> tenantNamesById)
    {
        var session = row.Session;
        return new OperationalSessionDto
        {
            Id = session.Id,
            SessionId = session.SessionId,
            TenantId = session.TenantId,
            TenantName = session.TenantId.HasValue && tenantNamesById.TryGetValue(session.TenantId.Value, out var tenantName)
                ? tenantName
                : null,
            UserId = session.UserId,
            UserName = row.User.UserName,
            Email = row.User.Email,
            DeviceId = session.DeviceId,
            ClientId = session.ClientId,
            IpAddress = session.IpAddress,
            UserAgent = session.UserAgent,
            CreationTime = session.CreationTime,
            LastActiveTime = session.LastActiveTime,
            Status = session.Status.ToString(),
            RevokedAtUtc = session.RevokedAtUtc,
            RevokedByUserId = session.RevokedByUserId,
            RevocationSource = session.RevocationSource,
            RevocationReason = session.RevocationReason
        };
    }

    private async Task<string> ComputeEffectiveStatusAsync(
        IdentitySessionStatus status,
        DateTime creationTime,
        Guid? tenantId,
        Guid userId,
        string sessionId)
    {
        if (status != IdentitySessionStatus.Active)
        {
            return status.ToString();
        }

        var absoluteExpiry = creationTime.AddHours(_sessionValidationOptions.Value.MarkerTtlHours);
        if (DateTime.UtcNow >= absoluteExpiry)
        {
            return IdentitySessionStatus.Expired.ToString();
        }

        var marker = await _sessionMarkerStore.IsActiveAsync(tenantId, userId, sessionId);
        if (marker == null)
        {
            return IdentitySessionStatus.Expired.ToString();
        }

        return IdentitySessionStatus.Active.ToString();
    }

    private static Guid? TryParseSessionId(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        return Guid.TryParse(sessionId, out var parsed) ? parsed : null;
    }

    private sealed class OperationalSessionQueryRow
    {
        public required IdentitySession Session { get; init; }
        public required ApplicationUser User { get; init; }
    }
}
