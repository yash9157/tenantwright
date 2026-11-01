using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Notifications.Application.Contracts.Presence;
using Notifications.Application.Contracts.Realtime;
using Notifications.Domain.Presence;
using Shared.Domain.Repositories;
using Shared.Modularity.DependencyInjection;

namespace Notifications.Presence;

public sealed class DistributedNotificationsPresenceStore : INotificationsPresenceStore, ITransientDependency
{
    private static readonly byte[] ActiveValue = [1];

    private readonly IDistributedCache _cache;
    private readonly IOptions<NotificationsPresenceOptions> _options;
    private readonly IRepository<NotificationPresenceRecord, Guid> _presenceRepository;

    public DistributedNotificationsPresenceStore(
        IDistributedCache cache,
        IOptions<NotificationsPresenceOptions> options,
        IRepository<NotificationPresenceRecord, Guid> presenceRepository)
    {
        _cache = cache;
        _options = options;
        _presenceRepository = presenceRepository;
    }

    public Task SetActiveAsync(Guid? tenantId, string userId, CancellationToken cancellationToken = default)
    {
        if (!_options.Value.Enabled)
        {
            return Task.CompletedTask;
        }

        var ttlSeconds = Math.Clamp(_options.Value.TtlSeconds, 10, 86400);
        var key = NotificationsRealtimeGroupNames.PresenceKey(tenantId, userId);
        return SetActiveCoreAsync(
            tenantId,
            userId,
            key,
            ttlSeconds,
            cancellationToken);
    }

    private async Task SetActiveCoreAsync(
        Guid? tenantId,
        string userId,
        string key,
        int ttlSeconds,
        CancellationToken cancellationToken)
    {
        await _cache.SetAsync(
            key,
            ActiveValue,
            new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(ttlSeconds)
            },
            cancellationToken);

        var now = DateTime.UtcNow;
        var records = await _presenceRepository.GetQueryableAsync();
        var record = await records.FirstOrDefaultAsync(
            x => x.TenantId == tenantId && x.UserId == userId,
            cancellationToken);
        if (record == null)
        {
            await _presenceRepository.InsertAsync(
                new NotificationPresenceRecord(Guid.CreateVersion7(), tenantId, userId, now),
                autoSave: true,
                cancellationToken);
            return;
        }

        record.MarkSeen(now);
        await _presenceRepository.UpdateAsync(record, autoSave: true, cancellationToken);
    }

    public async Task<bool> IsActiveAsync(Guid? tenantId, string userId, CancellationToken cancellationToken = default)
    {
        if (!_options.Value.Enabled)
        {
            return false;
        }

        var key = NotificationsRealtimeGroupNames.PresenceKey(tenantId, userId);
        var value = await _cache.GetAsync(key, cancellationToken);
        return value != null;
    }
}
