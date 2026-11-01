using AuditLogs.Application.Contracts.Auditing;
using AuditLogs.Domain.Auditing;
using AuditLogsErrorCodes = AuditLogs.Domain.Shared.Auditing.AuditLogsErrorCodes;
using Microsoft.EntityFrameworkCore;
using Shared.EntityFrameworkCore.TenantDatabases;

namespace AuditLogs.Application.Auditing;

/// <summary>
/// Sub-flow 4: read-only aggregation endpoints for the security dashboard (top users, failed actions,
/// risky IPs, volume-over-time). Tenant-scoped via the existing fail-closed <see cref="ResolveTenantFilter"/>
/// (host users may pass an explicit TenantId or omit it for a cross-tenant view; tenant users are pinned
/// to their own tenant). No schema change - pure aggregation over existing <see cref="AuditLog"/> rows.
/// </summary>
public sealed partial class AuditLogQueryAppService
{
    private const int DefaultSummaryWindowDays = 7;
    private const int MaxSummaryWindowDays = 90;

    /// <summary>
    /// The summary over every database that holds matching rows (see <see cref="QueryAuditDatabasesAsync{T}"/>),
    /// merged by <see cref="MergeSummaries"/> when the view spans dedicated tenant databases.
    /// </summary>
    public async Task<AuditLogSummaryReportDto> GetSummaryReportAsync(GetAuditLogSummaryInput input)
    {
        EnsureHostOperatorAccess();
        input ??= new GetAuditLogSummaryInput();
        var resolvedTenantId = ResolveTenantFilter(input.TenantId);
        var topCount = Math.Clamp(input.TopCount, 1, 100);
        var (fromUtc, toUtc) = NormalizeSummaryWindow(input.FromUtc, input.ToUtc);

        var summaries = await QueryAuditDatabasesAsync(
            resolvedTenantId,
            "auditlogs-console-summary",
            () => BuildDatabaseSummaryAsync(resolvedTenantId, topCount, fromUtc, toUtc));
        return summaries.Count == 1 ? summaries[0] : MergeSummaries(summaries, topCount, fromUtc, toUtc);
    }

    /// <summary>
    /// Combines per-database summaries. Totals and volume buckets are exact sums. Top lists are merged from each
    /// database's top <c>topCount</c> by summing per key: exact for keys owned by one database (users are
    /// tenant-owned), an approximation for keys that recur across databases (the same URL or client IP in several
    /// tenants), whose counts beyond one database's top list are not seen.
    /// </summary>
    private static AuditLogSummaryReportDto MergeSummaries(
        IReadOnlyList<AuditLogSummaryReportDto> summaries,
        int topCount,
        DateTime fromUtc,
        DateTime toUtc) =>
        new()
        {
            FromUtc = fromUtc,
            ToUtc = toUtc,
            GeneratedAtUtc = DateTime.UtcNow,
            TotalEvents = summaries.Sum(summary => summary.TotalEvents),
            FailedEvents = summaries.Sum(summary => summary.FailedEvents),
            TopUsers = summaries
                .SelectMany(summary => summary.TopUsers)
                .GroupBy(user => (user.UserId, user.UserName))
                .Select(group => new AuditLogTopUserDto
                {
                    UserId = group.Key.UserId,
                    UserName = group.Key.UserName,
                    EventCount = group.Sum(user => user.EventCount)
                })
                .OrderByDescending(user => user.EventCount)
                .Take(topCount)
                .ToList(),
            FailedActions = summaries
                .SelectMany(summary => summary.FailedActions)
                .GroupBy(action => (action.ApplicationName, action.Url, action.HttpStatusCode))
                .Select(group => new AuditLogFailedActionDto
                {
                    ApplicationName = group.Key.ApplicationName,
                    Url = group.Key.Url,
                    HttpStatusCode = group.Key.HttpStatusCode,
                    FailureCount = group.Sum(action => action.FailureCount)
                })
                .OrderByDescending(action => action.FailureCount)
                .Take(topCount)
                .ToList(),
            RiskyIps = summaries
                .SelectMany(summary => summary.RiskyIps)
                .GroupBy(ip => ip.ClientIpAddress, StringComparer.Ordinal)
                .Select(group => new AuditLogRiskyIpDto
                {
                    ClientIpAddress = group.Key,
                    FailureCount = group.Sum(ip => ip.FailureCount),
                    TotalCount = group.Sum(ip => ip.TotalCount),
                    DistinctUserCount = group.Sum(ip => ip.DistinctUserCount)
                })
                .OrderByDescending(ip => ip.FailureCount)
                .ThenByDescending(ip => ip.TotalCount)
                .Take(topCount)
                .ToList(),
            VolumeOverTime = summaries
                .SelectMany(summary => summary.VolumeOverTime)
                .GroupBy(point => point.BucketStartUtc)
                .Select(group => new AuditLogVolumePointDto
                {
                    BucketStartUtc = group.Key,
                    EventCount = group.Sum(point => point.EventCount),
                    FailedCount = group.Sum(point => point.FailedCount)
                })
                .OrderBy(point => point.BucketStartUtc)
                .ToList()
        };

    private async Task<AuditLogSummaryReportDto> BuildDatabaseSummaryAsync(
        Guid? resolvedTenantId,
        int topCount,
        DateTime fromUtc,
        DateTime toUtc)
    {
        var baseQuery = (await _queryStore.GetListQueryableAsync(
            new AuditLogs.Domain.Shared.Auditing.AuditLogFilter
            {
                TenantId = resolvedTenantId,
                FromUtc = fromUtc,
                ToUtc = toUtc
            },
            includeDetails: false))
            .WhereOwnedByCurrentDatabase();

        var totalEvents = await baseQuery.LongCountAsync();
        var failedEvents = await baseQuery
            .Where(x => x.HttpStatusCode.HasValue && x.HttpStatusCode.Value >= 400)
            .LongCountAsync();

        var topUsers = await baseQuery
            .Where(x => x.UserId != null)
            .GroupBy(x => new { x.UserId, x.UserName })
            .Select(g => new AuditLogTopUserDto
            {
                UserId = g.Key.UserId,
                UserName = g.Key.UserName,
                EventCount = g.LongCount()
            })
            .OrderByDescending(x => x.EventCount)
            .Take(topCount)
            .ToListAsync();

        var failedActions = await baseQuery
            .Where(x => x.HttpStatusCode.HasValue && x.HttpStatusCode.Value >= 400)
            .GroupBy(x => new { x.ApplicationName, x.Url, x.HttpStatusCode })
            .Select(g => new AuditLogFailedActionDto
            {
                ApplicationName = g.Key.ApplicationName,
                Url = g.Key.Url,
                HttpStatusCode = g.Key.HttpStatusCode,
                FailureCount = g.LongCount()
            })
            .OrderByDescending(x => x.FailureCount)
            .Take(topCount)
            .ToListAsync();

        var riskyIps = await baseQuery
            .Where(x => x.ClientIpAddress != null)
            .GroupBy(x => x.ClientIpAddress)
            .Select(g => new
            {
                ClientIpAddress = g.Key!,
                TotalCount = g.LongCount(),
                FailureCount = g.LongCount(x => x.HttpStatusCode.HasValue && x.HttpStatusCode.Value >= 400),
                DistinctUserCount = g.Select(x => x.UserId).Distinct().Count()
            })
            .OrderByDescending(x => x.FailureCount)
            .ThenByDescending(x => x.TotalCount)
            .Take(topCount)
            .ToListAsync();

        var volumeOverTime = await BuildVolumeOverTimeAsync(baseQuery, fromUtc, toUtc);
        var generatedAtUtc = DateTime.UtcNow;

        return new AuditLogSummaryReportDto
        {
            FromUtc = fromUtc,
            ToUtc = toUtc,
            GeneratedAtUtc = generatedAtUtc,
            TotalEvents = totalEvents,
            FailedEvents = failedEvents,
            TopUsers = topUsers,
            FailedActions = failedActions,
            RiskyIps = riskyIps
                .Select(x => new AuditLogRiskyIpDto
                {
                    ClientIpAddress = x.ClientIpAddress,
                    FailureCount = x.FailureCount,
                    TotalCount = x.TotalCount,
                    DistinctUserCount = x.DistinctUserCount
                })
                .ToList(),
            VolumeOverTime = volumeOverTime
        };
    }

    private static async Task<List<AuditLogVolumePointDto>> BuildVolumeOverTimeAsync(
        IQueryable<AuditLog> baseQuery,
        DateTime fromUtc,
        DateTime toUtc)
    {
        // Day-level grouping (x.ExecutionTime.Date) is DB-translatable and keeps the result set small
        // regardless of row volume - this is the only aggregation used for windows over 2 days, which
        // covers the documented max window (90 days -> at most 90 grouped rows). For short windows
        // (<=2 days) we additionally re-bucket to hourly server-side by grouping on Date + Hour, still
        // fully translatable and bounded to <=48 rows.
        var windowSpan = toUtc - fromUtc;
        if (windowSpan.TotalDays <= 2)
        {
            var hourly = await baseQuery
                .GroupBy(x => new { x.ExecutionTime.Date, x.ExecutionTime.Hour })
                .Select(g => new AuditLogVolumePointDto
                {
                    BucketStartUtc = g.Key.Date.AddHours(g.Key.Hour),
                    EventCount = g.LongCount(),
                    FailedCount = g.LongCount(x => x.HttpStatusCode.HasValue && x.HttpStatusCode.Value >= 400)
                })
                .OrderBy(x => x.BucketStartUtc)
                .ToListAsync();
            return hourly;
        }

        var daily = await baseQuery
            .GroupBy(x => x.ExecutionTime.Date)
            .Select(g => new AuditLogVolumePointDto
            {
                BucketStartUtc = g.Key,
                EventCount = g.LongCount(),
                FailedCount = g.LongCount(x => x.HttpStatusCode.HasValue && x.HttpStatusCode.Value >= 400)
            })
            .OrderBy(x => x.BucketStartUtc)
            .ToListAsync();

        if (windowSpan.TotalDays <= 31)
        {
            return daily;
        }

        // Weekly re-bucketing of the (already small, <=90-row) daily result - safe to do in memory.
        var weekly = new SortedDictionary<DateTime, (long EventCount, long FailedCount)>();
        foreach (var point in daily)
        {
            var elapsedDays = (point.BucketStartUtc - fromUtc.Date).Days;
            var weekIndex = elapsedDays < 0 ? 0 : elapsedDays / 7;
            var weekStart = fromUtc.Date.AddDays(weekIndex * 7);
            weekly.TryGetValue(weekStart, out var counts);
            weekly[weekStart] = (
                counts.EventCount + point.EventCount,
                counts.FailedCount + point.FailedCount);
        }

        return weekly
            .Select(pair => new AuditLogVolumePointDto
            {
                BucketStartUtc = pair.Key,
                EventCount = pair.Value.EventCount,
                FailedCount = pair.Value.FailedCount
            })
            .OrderBy(x => x.BucketStartUtc)
            .ToList();
    }

    private static (DateTime FromUtc, DateTime ToUtc) NormalizeSummaryWindow(DateTime? fromUtc, DateTime? toUtc)
    {
        var normalizedTo = NormalizeUtc(toUtc) ?? DateTime.UtcNow;
        var normalizedFrom = NormalizeUtc(fromUtc) ?? normalizedTo.AddDays(-DefaultSummaryWindowDays);

        if (normalizedFrom > normalizedTo)
        {
            (normalizedFrom, normalizedTo) = (normalizedTo, normalizedFrom);
        }

        if ((normalizedTo - normalizedFrom).TotalDays > MaxSummaryWindowDays)
        {
            throw CreateValidationException(
                AuditLogsErrorCodes.DashboardWindowExceeded,
                $"Summary report date range cannot exceed {MaxSummaryWindowDays} days.");
        }

        return (normalizedFrom, normalizedTo);
    }
}
