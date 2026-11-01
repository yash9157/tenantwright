using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Notifications.Application.Contracts.Delivery;
using Notifications.Application.Delivery;
using Notifications.Domain.Digests;
using Notifications.Domain.Shared.Enums;
using Notifications.EntityFrameworkCore.EntityFrameworkCore;
using Shared.EntityFrameworkCore;
using Shared.EntityFrameworkCore.TenantDatabases;
using Shared.EventContracts;
using Shared.Modularity.DependencyInjection;
using Shared.Uow.Abstractions;
using System.Globalization;

namespace Notifications.Application.Digests;

public sealed class NotificationDigestProcessor : ITransientDependency
{
    private readonly IDbContextProvider<NotificationsDbContextApp> _dbContextProvider;
    private readonly IUnitOfWorkExecutor _unitOfWorkExecutor;
    private readonly INotificationDeliveryScheduler _scheduler;
    private readonly IOptionsMonitor<NotificationsDigestOptions> _options;

    public NotificationDigestProcessor(
        IDbContextProvider<NotificationsDbContextApp> dbContextProvider,
        IUnitOfWorkExecutor unitOfWorkExecutor,
        INotificationDeliveryScheduler scheduler,
        IOptionsMonitor<NotificationsDigestOptions> options)
    {
        _dbContextProvider = dbContextProvider;
        _unitOfWorkExecutor = unitOfWorkExecutor;
        _scheduler = scheduler;
        _options = options;
    }

    public async Task<int> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        if (!options.Enabled)
        {
            return 0;
        }

        var now = DateTime.UtcNow;
        var bucketIds = await _unitOfWorkExecutor.ExecuteNonTransactionalAsync(
            async operationToken =>
            {
                var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
                // Under database fan-out only buckets this database owns emit: an emitted digest becomes a
                // notification for the bucket's tenant, which must be created where that tenant's data lives.
                return await unitOfWorkDbContext.NotificationDigestBuckets
                    .IgnoreQueryFilters()
                    .WhereOwnedByCurrentDatabase()
                    .Where(x =>
                        (x.Status == NotificationDigestBucketStatus.Open ||
                         x.Status == NotificationDigestBucketStatus.Processing) &&
                        x.PeriodEndUtc <= now)
                    .OrderBy(x => x.PeriodEndUtc)
                    .ThenBy(x => x.Id)
                    .Select(x => x.Id)
                    .Take(Math.Clamp(options.BatchSize, 1, 500))
                    .ToListAsync(operationToken);
            },
            cancellationToken);

        var processed = 0;
        foreach (var bucketId in bucketIds)
        {
            if (await ProcessBucketAsync(bucketId, now, cancellationToken))
            {
                processed++;
            }
        }

        return processed;
    }

    private async Task<bool> ProcessBucketAsync(
        Guid bucketId,
        DateTime occurredAtUtc,
        CancellationToken cancellationToken)
    {
        var bucket = await TryClaimBucketAsync(bucketId, occurredAtUtc, cancellationToken);
        if (bucket == null)
        {
            return false;
        }

        try
        {
            var itemCount = await CountItemsAsync(bucket.Id, cancellationToken);
            if (itemCount == 0)
            {
                return await CloseEmptyBucketAsync(bucket.Id, occurredAtUtc, cancellationToken);
            }

            // Scheduling performs feature/preference/audience/rate-limit work and owns its own
            // bounded persistence scope. It must never run inside the digest claim/finalize transaction.
            var result = await NotificationContractFactory.ScheduleAsync(
                _scheduler,
                bucket.TenantId,
                new NotificationSendRequestedHostEto
                {
                    RequestId = bucket.Id,
                    Channel = MapChannel(bucket.Channel),
                    Subject = "Notification digest",
                    TemplateName = "Notifications.Digest",
                    Category = "Notifications.Digest",
                    Priority = NotificationSendPriority.Normal,
                    SourceService = "notifications-digest",
                    OccurredAtUtc = occurredAtUtc,
                    TemplateModel = new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        ["count"] = itemCount.ToString(CultureInfo.InvariantCulture),
                        ["periodStartUtc"] = bucket.PeriodStartUtc.ToString("O"),
                        ["periodEndUtc"] = bucket.PeriodEndUtc.ToString("O")
                    },
                    Recipients =
                    [
                        new NotificationRecipientEto
                        {
                            RecipientType = MapRecipientType(bucket.RecipientType),
                            RecipientValue = bucket.RecipientValue
                        }
                    ]
                },
                cancellationToken);
            if (!result.Accepted || result.NotificationId == Guid.Empty)
            {
                throw new InvalidOperationException("Digest delivery was not accepted.");
            }

            return await MarkBucketEmittedAsync(
                bucket.Id,
                result.NotificationId,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // A Processing bucket is deliberately recoverable. Do not perform canceled cleanup
            // that could mask shutdown or create another partial state transition.
            throw;
        }
        catch (Exception processingException)
        {
            try
            {
                await ReopenBucketAsync(bucket.Id, CancellationToken.None);
            }
            catch (Exception recoveryException)
            {
                throw new AggregateException(
                    "Digest processing and recovery both failed.",
                    processingException,
                    recoveryException);
            }

            throw;
        }
    }

    private Task<ClaimedDigestBucket?> TryClaimBucketAsync(
        Guid bucketId,
        DateTime occurredAtUtc,
        CancellationToken cancellationToken)
        => _unitOfWorkExecutor.ExecuteTransactionalAsync(
            async operationToken =>
            {
                var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
                var bucket = await unitOfWorkDbContext.NotificationDigestBuckets
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(
                        x => x.Id == bucketId &&
                             (x.Status == NotificationDigestBucketStatus.Open ||
                              x.Status == NotificationDigestBucketStatus.Processing) &&
                             x.PeriodEndUtc <= occurredAtUtc,
                        operationToken);
                if (bucket == null)
                {
                    return null;
                }

                bucket.MarkProcessing();
                return new ClaimedDigestBucket(
                    bucket.Id,
                    bucket.TenantId,
                    bucket.Channel,
                    bucket.RecipientType,
                    bucket.RecipientValue,
                    bucket.PeriodStartUtc,
                    bucket.PeriodEndUtc);
            },
            cancellationToken);

    private Task<int> CountItemsAsync(Guid bucketId, CancellationToken cancellationToken)
        => _unitOfWorkExecutor.ExecuteNonTransactionalAsync(
            async operationToken =>
            {
                var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
                return await unitOfWorkDbContext.NotificationDigestItems
                    .IgnoreQueryFilters()
                    .CountAsync(x => x.BucketId == bucketId, operationToken);
            },
            cancellationToken);

    private Task<bool> CloseEmptyBucketAsync(
        Guid bucketId,
        DateTime closedAtUtc,
        CancellationToken cancellationToken)
        => _unitOfWorkExecutor.ExecuteTransactionalAsync(
            async operationToken =>
            {
                var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
                var bucket = await unitOfWorkDbContext.NotificationDigestBuckets
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(
                        x => x.Id == bucketId && x.Status == NotificationDigestBucketStatus.Processing,
                        operationToken);
                if (bucket == null)
                {
                    return false;
                }

                bucket.MarkClosedEmpty(closedAtUtc);
                return true;
            },
            cancellationToken);

    private Task<bool> MarkBucketEmittedAsync(
        Guid bucketId,
        Guid notificationId,
        CancellationToken cancellationToken)
        => _unitOfWorkExecutor.ExecuteTransactionalAsync(
            async operationToken =>
            {
                var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
                var bucket = await unitOfWorkDbContext.NotificationDigestBuckets
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(
                        x => x.Id == bucketId && x.Status == NotificationDigestBucketStatus.Processing,
                        operationToken);
                if (bucket == null)
                {
                    return false;
                }

                bucket.MarkEmitted(notificationId, DateTime.UtcNow);
                return true;
            },
            cancellationToken);

    private Task ReopenBucketAsync(Guid bucketId, CancellationToken cancellationToken)
        => _unitOfWorkExecutor.ExecuteTransactionalAsync(
            async operationToken =>
            {
                var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
                var bucket = await unitOfWorkDbContext.NotificationDigestBuckets
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(
                        x => x.Id == bucketId && x.Status == NotificationDigestBucketStatus.Processing,
                        operationToken);
                bucket?.Reopen();
            },
            cancellationToken);

    private static NotificationSendChannel MapChannel(NotificationChannel channel) => channel switch
    {
        NotificationChannel.Email => NotificationSendChannel.Email,
        NotificationChannel.Sms => NotificationSendChannel.Sms,
        NotificationChannel.Push => NotificationSendChannel.Push,
        _ => throw new InvalidOperationException($"Channel '{channel}' is not digest eligible.")
    };

    private static NotificationSendRecipientType MapRecipientType(NotificationRecipientType recipientType) => recipientType switch
    {
        NotificationRecipientType.EmailAddress => NotificationSendRecipientType.EmailAddress,
        NotificationRecipientType.PhoneNumber => NotificationSendRecipientType.PhoneNumber,
        NotificationRecipientType.DeviceToken => NotificationSendRecipientType.DeviceToken,
        _ => throw new InvalidOperationException($"Recipient type '{recipientType}' is not valid for an external digest.")
    };

    private sealed record ClaimedDigestBucket(
        Guid Id,
        Guid? TenantId,
        NotificationChannel Channel,
        NotificationRecipientType RecipientType,
        string RecipientValue,
        DateTime PeriodStartUtc,
        DateTime PeriodEndUtc);
}
