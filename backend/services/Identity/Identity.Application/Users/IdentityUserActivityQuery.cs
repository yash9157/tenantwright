using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Identity.Application.Contracts.Users;
using Identity.Domain.Shared.ApplicationUsers;
using Identity.EntityFrameworkCore.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Identity.Application.Users;

/// <summary>
/// Batched, N+1-free derivation of per-user activity (last successful login + last session
/// activity) from already-persisted security logs and sessions. Used to enrich the user list and
/// detail projections without touching the login/auth write path.
/// </summary>
internal static class IdentityUserActivityQuery
{
    /// <summary>Security-log action recorded on a successful sign-in.</summary>
    public const string LoginSuccessAction = "LoginSuccess";

    public static Task<Dictionary<Guid, UserActivitySnapshot>> LoadAsync(
        IdentityDbContextApp dbContext,
        IReadOnlyCollection<Guid> userIds,
        CancellationToken cancellationToken = default)
        => LoadCoreAsync(dbContext, userIds, acrossTenants: false, cancellationToken);

    public static Task<Dictionary<Guid, UserActivitySnapshot>> LoadAcrossTenantsAsync(
        IdentityDbContextApp dbContext,
        IReadOnlyCollection<Guid> userIds,
        CancellationToken cancellationToken = default)
        => LoadCoreAsync(dbContext, userIds, acrossTenants: true, cancellationToken);

    private static async Task<Dictionary<Guid, UserActivitySnapshot>> LoadCoreAsync(
        IdentityDbContextApp dbContext,
        IReadOnlyCollection<Guid> userIds,
        bool acrossTenants,
        CancellationToken cancellationToken)
    {
        if (userIds.Count == 0)
        {
            return [];
        }

        var ids = userIds.Distinct().ToList();
        var tenantId = dbContext.CurrentTenant.Id;
        var securityLogs = acrossTenants
            ? dbContext.IdentitySecurityLogs.IgnoreQueryFilters()
            : dbContext.IdentitySecurityLogs.AsQueryable();
        var sessions = acrossTenants
            ? dbContext.IdentitySessions.IgnoreQueryFilters()
            : dbContext.IdentitySessions.AsQueryable();

        if (!acrossTenants)
        {
            securityLogs = securityLogs.Where(log => log.TenantId == tenantId);
            sessions = sessions.Where(session => session.TenantId == tenantId);
        }

        var lastLogins = await securityLogs
            .AsNoTracking()
            .Where(x => x.UserId != null && ids.Contains(x.UserId.Value) && x.Action == LoginSuccessAction)
            .GroupBy(x => x.UserId!.Value)
            .Select(group => new { UserId = group.Key, Value = (DateTime?)group.Max(x => x.CreationTime) })
            .ToListAsync(cancellationToken);

        var lastActives = await sessions.AsNoTracking()
            .Where(x => ids.Contains(x.UserId))
            .GroupBy(x => x.UserId)
            .Select(group => new { UserId = group.Key, Value = (DateTime?)group.Max(x => x.LastActiveTime) })
            .ToListAsync(cancellationToken);

        var map = new Dictionary<Guid, UserActivitySnapshot>();
        foreach (var row in lastLogins)
        {
            map[row.UserId] = new UserActivitySnapshot { LastLoginAtUtc = row.Value };
        }

        foreach (var row in lastActives)
        {
            map[row.UserId] = map.TryGetValue(row.UserId, out var existing)
                ? existing with { LastActiveAtUtc = row.Value }
                : new UserActivitySnapshot { LastActiveAtUtc = row.Value };
        }

        return map;
    }

    public static Task<int> CountActiveSessionsAsync(
        IdentityDbContextApp dbContext,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var tenantId = dbContext.CurrentTenant.Id;
        return dbContext.IdentitySessions
            .AsNoTracking()
            .CountAsync(x => x.TenantId == tenantId && x.UserId == userId && x.Status == IdentitySessionStatus.Active, cancellationToken);
    }

    /// <summary>
    /// Zero-filled daily successful-sign-in counts for one user over the window (C9),
    /// derived from already-persisted security logs. Shape, not magnitude, is the signal.
    /// </summary>
    public static async Task<List<UserSignInActivityPointDto>> LoadSignInSeriesAsync(
        IdentityDbContextApp dbContext,
        Guid userId,
        int windowDays,
        CancellationToken cancellationToken = default)
    {
        var windowStartUtc = DateTime.UtcNow.Date.AddDays(-(windowDays - 1));
        var tenantId = dbContext.CurrentTenant.Id;
        var byDay = (await dbContext.IdentitySecurityLogs
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.UserId == userId && x.Action == LoginSuccessAction && x.CreationTime >= windowStartUtc)
            .GroupBy(x => x.CreationTime.Date)
            .Select(g => new { Day = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken))
            .ToDictionary(x => x.Day, x => x.Count);

        var series = new List<UserSignInActivityPointDto>(windowDays);
        for (var offset = 0; offset < windowDays; offset++)
        {
            var day = windowStartUtc.AddDays(offset).Date;
            series.Add(new UserSignInActivityPointDto { DateUtc = day, Count = byDay.GetValueOrDefault(day) });
        }

        return series;
    }
}

internal sealed record UserActivitySnapshot
{
    public DateTime? LastLoginAtUtc { get; init; }
    public DateTime? LastActiveAtUtc { get; init; }
}
