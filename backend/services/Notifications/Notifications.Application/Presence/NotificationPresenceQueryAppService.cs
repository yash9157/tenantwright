using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Notifications.Application.Contracts.Common;
using Notifications.Application.Contracts.Presence;
using Notifications.Domain.Presence;
using Shared.Domain.Repositories;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;

namespace Notifications.Application.Presence;

[DisableConventionalRegistration]
public sealed class NotificationPresenceQueryAppService : INotificationPresenceQueryAppService, ITransientDependency
{
    private const int DefaultPageSize = 25;
    private const int MaxPageSize = 100;
    private const int DefaultStaleAfterMinutes = 60;

    private readonly IRepository<NotificationPresenceRecord, Guid> _presenceRepository;
    private readonly IOptions<NotificationsPresenceOptions> _presenceOptions;
    private readonly ICurrentTenant _currentTenant;

    public NotificationPresenceQueryAppService(
        IRepository<NotificationPresenceRecord, Guid> presenceRepository,
        IOptions<NotificationsPresenceOptions> presenceOptions,
        ICurrentTenant currentTenant)
    {
        _presenceRepository = presenceRepository;
        _presenceOptions = presenceOptions;
        _currentTenant = currentTenant;
    }

    public async Task<NotificationPresenceSummaryDto> GetSummaryAsync(
        GetNotificationPresenceInput input,
        CancellationToken cancellationToken = default)
    {
        var context = ResolveContext(input);
        var query = await BuildFilteredQueryAsync(context.TenantId, input.Filter);

        var totalCount = await query.CountAsync(cancellationToken);
        var activeCount = await query.CountAsync(x => x.LastSeenAtUtc >= context.ActiveCutoffUtc, cancellationToken);
        var staleCount = await query.CountAsync(
            x => x.LastSeenAtUtc < context.ActiveCutoffUtc && x.LastSeenAtUtc >= context.StaleCutoffUtc,
            cancellationToken);
        var latestHeartbeatAtUtc = await query.MaxAsync(x => (DateTime?)x.LastSeenAtUtc, cancellationToken);
        var inactiveCount = totalCount - activeCount - staleCount;

        return new NotificationPresenceSummaryDto
        {
            TenantId = context.TenantId,
            ActiveCount = activeCount,
            StaleCount = staleCount,
            InactiveCount = inactiveCount,
            TotalCount = totalCount,
            LatestHeartbeatAtUtc = latestHeartbeatAtUtc,
            TtlSeconds = context.TtlSeconds,
            StaleAfterMinutes = context.StaleAfterMinutes
        };
    }

    public async Task<PagedResultDto<NotificationPresenceItemDto>> GetListAsync(
        GetNotificationPresenceInput input,
        CancellationToken cancellationToken = default)
    {
        var context = ResolveContext(input);
        var query = await BuildFilteredQueryAsync(context.TenantId, input.Filter);

        if (input.Status.HasValue)
        {
            query = input.Status.Value switch
            {
                NotificationPresenceStatus.Active => query.Where(x => x.LastSeenAtUtc >= context.ActiveCutoffUtc),
                NotificationPresenceStatus.Stale => query.Where(x => x.LastSeenAtUtc < context.ActiveCutoffUtc && x.LastSeenAtUtc >= context.StaleCutoffUtc),
                NotificationPresenceStatus.Inactive => query.Where(x => x.LastSeenAtUtc < context.StaleCutoffUtc),
                _ => query
            };
        }

        var totalCount = await query.LongCountAsync(cancellationToken);
        var skip = Math.Max(input.SkipCount, 0);
        var take = input.MaxResultCount <= 0 ? DefaultPageSize : Math.Min(input.MaxResultCount, MaxPageSize);

        var records = await query
            .OrderByDescending(x => x.LastSeenAtUtc)
            .ThenBy(x => x.UserId)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);

        return new PagedResultDto<NotificationPresenceItemDto>
        {
            TotalCount = totalCount,
            Items = records.Select(record => Map(record, context)).ToList()
        };
    }

    private async Task<IQueryable<NotificationPresenceRecord>> BuildFilteredQueryAsync(Guid? tenantId, string? filter)
    {
        var query = (await _presenceRepository.GetQueryableAsync())
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId);

        var normalizedFilter = filter?.Trim();
        if (!string.IsNullOrWhiteSpace(normalizedFilter))
        {
            var lowered = normalizedFilter.ToLowerInvariant();
            query = query.Where(x => x.UserId.ToLower().Contains(lowered));
        }

        return query;
    }

    private PresenceQueryContext ResolveContext(GetNotificationPresenceInput input)
    {
        var tenantId = input.TenantId;
        if (_currentTenant.Id.HasValue)
        {
            tenantId ??= _currentTenant.Id;
            if (tenantId != _currentTenant.Id)
            {
                throw new UnauthorizedAccessException("Cross-tenant presence queries are not allowed.");
            }
        }

        var ttlSeconds = Math.Clamp(_presenceOptions.Value.TtlSeconds, 10, 86400);
        var staleAfterMinutes = Math.Clamp(input.StaleAfterMinutes ?? DefaultStaleAfterMinutes, 5, 10080);
        var now = DateTime.UtcNow;

        return new PresenceQueryContext(
            tenantId,
            ttlSeconds,
            staleAfterMinutes,
            now,
            now.AddSeconds(-ttlSeconds),
            now.AddMinutes(-staleAfterMinutes));
    }

    private static NotificationPresenceItemDto Map(NotificationPresenceRecord record, PresenceQueryContext context)
    {
        var status = record.LastSeenAtUtc >= context.ActiveCutoffUtc
            ? NotificationPresenceStatus.Active
            : record.LastSeenAtUtc >= context.StaleCutoffUtc
                ? NotificationPresenceStatus.Stale
                : NotificationPresenceStatus.Inactive;

        return new NotificationPresenceItemDto
        {
            Id = record.Id,
            TenantId = record.TenantId,
            UserId = record.UserId,
            Status = status,
            LastSeenAtUtc = record.LastSeenAtUtc,
            ExpiresAtUtc = record.LastSeenAtUtc.AddSeconds(context.TtlSeconds),
            HeartbeatAgeSeconds = Math.Max(0, Convert.ToInt64((context.NowUtc - record.LastSeenAtUtc).TotalSeconds))
        };
    }

    private sealed record PresenceQueryContext(
        Guid? TenantId,
        int TtlSeconds,
        int StaleAfterMinutes,
        DateTime NowUtc,
        DateTime ActiveCutoffUtc,
        DateTime StaleCutoffUtc);
}
