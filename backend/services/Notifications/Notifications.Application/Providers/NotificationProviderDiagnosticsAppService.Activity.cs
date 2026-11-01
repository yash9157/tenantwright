using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Notifications.Application.Contracts.Common;
using Notifications.Application.Contracts.Providers;
using Notifications.Domain.Notifications;
using Notifications.Domain.Shared.Constants;
using Notifications.Domain.Shared.Enums;
using Shared.Domain.Repositories;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;

namespace Notifications.Application.Providers;

public sealed partial class NotificationProviderDiagnosticsAppService
{
    public async Task<PagedResultDto<NotificationProviderActivityItemDto>> GetRecentActivityAsync(
        GetNotificationProviderActivityInput input,
        CancellationToken cancellationToken = default)
    {
        input.TenantId = ResolveTenantScope(input.TenantId);
        using var tenantDatabase = _tenantDatabases.EnterDatabase(await FindHostTargetTenantDatabaseAsync(input.TenantId, cancellationToken));
        using var tenantFilterScope = BeginTenantFilterScope(input.TenantId);

        var notifications = await _notificationRepository.GetQueryableAsync();
        var deliveries = await _deliveryRepository.GetQueryableAsync();
        var effectiveChannels = await GetEffectiveChannelsAsync(input.TenantId, cancellationToken);

        var query = notifications
            .AsNoTracking()
            .Where(x => effectiveChannels.Contains(x.Channel) && x.TenantId == input.TenantId);

        if (input.FromUtc.HasValue)
        {
            query = query.Where(x => x.CreationTime >= input.FromUtc.Value);
        }

        if (input.ToUtc.HasValue)
        {
            query = query.Where(x => x.CreationTime <= input.ToUtc.Value);
        }

        var templateName = input.TemplateName?.Trim();
        if (!string.IsNullOrWhiteSpace(templateName))
        {
            var normalizedTemplateName = templateName.ToLowerInvariant();
            query = query.Where(x => x.TemplateName.ToLower().Contains(normalizedTemplateName));
        }

        var filter = input.Filter?.Trim();
        if (!string.IsNullOrWhiteSpace(filter))
        {
            var normalizedFilter = filter.ToLowerInvariant();
            query = query.Where(x =>
                x.TemplateName.ToLower().Contains(normalizedFilter) ||
                (x.Subject != null && x.Subject.ToLower().Contains(normalizedFilter)));
        }

        if (input.Status.HasValue)
        {
            query = query.Where(notification => deliveries.Any(delivery =>
                delivery.NotificationId == notification.Id &&
                delivery.Status == input.Status.Value));
        }

        if (input.SourceType.HasValue)
        {
            query = input.SourceType.Value == NotificationProviderActivitySourceType.Runtime
                ? query
                : query.Where(_ => false);
        }

        var totalCount = await query.LongCountAsync(cancellationToken);
        var skip = Math.Max(input.SkipCount, 0);
        var take = input.MaxResultCount <= 0 ? DefaultPageSize : Math.Min(input.MaxResultCount, MaxPageSize);

        var items = await query
            .OrderByDescending(x => x.CreationTime)
            .ThenByDescending(x => x.Id)
            .Skip(skip)
            .Take(take)
            .Select(notification => new NotificationProviderActivityItemDto
            {
                NotificationId = notification.Id,
                RequestId = notification.RequestId,
                TenantId = notification.TenantId,
                Channel = notification.Channel,
                TemplateName = notification.TemplateName,
                Subject = notification.Subject,
                SourceType = NotificationProviderActivitySourceType.Runtime,
                Status = deliveries.Any(delivery => delivery.NotificationId == notification.Id && delivery.Status == NotificationStatus.Failed)
                    ? NotificationStatus.Failed
                    : deliveries.Any(delivery => delivery.NotificationId == notification.Id && delivery.Status == NotificationStatus.Processing)
                        ? NotificationStatus.Processing
                        : deliveries.Any(delivery => delivery.NotificationId == notification.Id && delivery.Status == NotificationStatus.Queued)
                            ? NotificationStatus.Queued
                            : deliveries.Any(delivery => delivery.NotificationId == notification.Id &&
                                  delivery.Status == NotificationStatus.Delivered)
                                ? NotificationStatus.Delivered
                                : NotificationStatus.ProviderAccepted,
                CreationTime = notification.CreationTime,
                DeliveryCount = deliveries.Count(delivery => delivery.NotificationId == notification.Id),
                QueuedCount = deliveries.Count(delivery => delivery.NotificationId == notification.Id && delivery.Status == NotificationStatus.Queued),
                ProcessingCount = deliveries.Count(delivery => delivery.NotificationId == notification.Id && delivery.Status == NotificationStatus.Processing),
                SentCount = deliveries.Count(delivery => delivery.NotificationId == notification.Id &&
                    (delivery.Status == NotificationStatus.ProviderAccepted ||
                     delivery.Status == NotificationStatus.Delivered)),
                FailedCount = deliveries.Count(delivery => delivery.NotificationId == notification.Id && delivery.Status == NotificationStatus.Failed),
                LatestAttemptAtUtc = deliveries
                    .Where(delivery => delivery.NotificationId == notification.Id && delivery.LastTryTimeUtc.HasValue)
                    .Max(delivery => (DateTime?)delivery.LastTryTimeUtc),
                LatestSuccessAtUtc = deliveries
                    .Where(delivery => delivery.NotificationId == notification.Id &&
                        (delivery.Status == NotificationStatus.ProviderAccepted ||
                         delivery.Status == NotificationStatus.Delivered) && delivery.SentTimeUtc.HasValue)
                    .Max(delivery => (DateTime?)delivery.SentTimeUtc),
                LatestFailureAtUtc = deliveries
                    .Where(delivery => delivery.NotificationId == notification.Id && delivery.Status == NotificationStatus.Failed && delivery.LastTryTimeUtc.HasValue)
                    .Max(delivery => (DateTime?)delivery.LastTryTimeUtc),
                LatestFailureError = deliveries
                    .Where(delivery => delivery.NotificationId == notification.Id && delivery.Status == NotificationStatus.Failed && delivery.LastTryTimeUtc.HasValue)
                    .OrderByDescending(delivery => delivery.LastTryTimeUtc)
                    .ThenByDescending(delivery => delivery.Id)
                    .Select(delivery => delivery.LastError)
                    .FirstOrDefault()
            })
            .ToListAsync(cancellationToken);

        return new PagedResultDto<NotificationProviderActivityItemDto>
        {
            Items = items,
            TotalCount = totalCount
        };
    }

}
