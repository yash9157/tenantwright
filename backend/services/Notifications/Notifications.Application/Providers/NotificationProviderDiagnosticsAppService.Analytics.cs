using Microsoft.EntityFrameworkCore;
using Notifications.Application.Contracts.Providers;
using Notifications.Domain.Shared.Enums;

namespace Notifications.Application.Providers;

public sealed partial class NotificationProviderDiagnosticsAppService
{
    public async Task<NotificationProviderAnalyticsDto> GetAnalyticsAsync(
        GetNotificationProviderAnalyticsInput input,
        CancellationToken cancellationToken = default)
    {
        input.TenantId = ResolveTenantScope(input.TenantId);
        using var tenantDatabase = _tenantDatabases.EnterDatabase(await FindHostTargetTenantDatabaseAsync(input.TenantId, cancellationToken));
        using var tenantFilterScope = BeginTenantFilterScope(input.TenantId);

        var effectiveChannels = await GetEffectiveChannelsAsync(input.TenantId, cancellationToken);
        if (input.Channel.HasValue && !effectiveChannels.Contains(input.Channel.Value))
        {
            return CreateEmptyAnalytics(input);
        }

        var emailConfig = effectiveChannels.Contains(NotificationChannel.Email)
            ? await GetLatestEmailConfigAsync(input.TenantId, cancellationToken)
            : null;
        var emailProviderLabel = ResolveProviderLabel(emailConfig);
        var providerChannel = ResolveProviderChannel(
            input.Provider,
            input.Channel,
            effectiveChannels,
            emailConfig,
            emailProviderLabel);
        if (!string.IsNullOrWhiteSpace(input.Provider) && !providerChannel.HasValue)
        {
            return CreateEmptyAnalytics(input);
        }

        var query = await BuildAnalyticsQueryAsync(input, effectiveChannels);
        if (providerChannel.HasValue)
        {
            query = query.Where(x => x.Channel == providerChannel.Value);
        }

        var totalDeliveryCount = await query.CountAsync(cancellationToken);
        var totalNotificationCount = await query
            .Select(x => x.NotificationId)
            .Distinct()
            .CountAsync(cancellationToken);
        var queuedCount = await query.CountAsync(x => x.DeliveryStatus == NotificationStatus.Queued, cancellationToken);
        var processingCount = await query.CountAsync(x => x.DeliveryStatus == NotificationStatus.Processing, cancellationToken);
        var sentCount = await query.CountAsync(x =>
            x.DeliveryStatus == NotificationStatus.ProviderAccepted ||
            x.DeliveryStatus == NotificationStatus.Delivered, cancellationToken);
        var failedCount = await query.CountAsync(x => x.DeliveryStatus == NotificationStatus.Failed, cancellationToken);
        var retriedDeliveryCount = await query.CountAsync(x => x.TryCount > 1, cancellationToken);
        var affectedTemplateCount = await query
            .Select(x => x.TemplateName)
            .Distinct()
            .CountAsync(cancellationToken);
        var affectedProviderCount = await query
            .Select(x => x.Channel)
            .Distinct()
            .CountAsync(cancellationToken);
        var last24HoursUtc = DateTime.UtcNow.AddHours(-24);
        var last24HoursQuery = await BuildAnalyticsQueryAsync(
            input,
            effectiveChannels,
            includeReportingWindow: false);
        if (providerChannel.HasValue)
        {
            last24HoursQuery = last24HoursQuery.Where(x => x.Channel == providerChannel.Value);
        }

        last24HoursQuery = last24HoursQuery.Where(x =>
            x.DeliveryAttemptedAtUtc.HasValue && x.DeliveryAttemptedAtUtc.Value >= last24HoursUtc);
        var last24HoursDeliveryCount = await last24HoursQuery.CountAsync(cancellationToken);
        var last24HoursSentCount = await last24HoursQuery
            .CountAsync(x => x.DeliveryStatus == NotificationStatus.ProviderAccepted ||
                x.DeliveryStatus == NotificationStatus.Delivered, cancellationToken);

        var statusMix = await query
            .GroupBy(x => x.DeliveryStatus)
            .Select(group => new NotificationProviderAnalyticsStatusMixDto
            {
                Status = group.Key,
                Count = group.Count()
            })
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Status)
            .ToListAsync(cancellationToken);
        foreach (var item in statusMix)
        {
            item.Percentage = CalculateRate(item.Count, totalDeliveryCount);
        }

        var deliveryVolume = await query
            .GroupBy(x => x.CreationTime.Date)
            .Select(group => new NotificationProviderAnalyticsTimeBucketDto
            {
                BucketUtc = group.Key,
                TotalCount = group.Count(),
                SentCount = group.Count(x => x.DeliveryStatus == NotificationStatus.ProviderAccepted ||
                    x.DeliveryStatus == NotificationStatus.Delivered),
                FailedCount = group.Count(x => x.DeliveryStatus == NotificationStatus.Failed),
                RetriedCount = group.Count(x => x.TryCount > 1)
            })
            .OrderBy(x => x.BucketUtc)
            .ToListAsync(cancellationToken);

        var topFailingTemplates = await query
            .GroupBy(x => x.TemplateName)
            .Select(group => new NotificationProviderAnalyticsTopFailureDto
            {
                Key = group.Key,
                FailedCount = group.Count(x => x.DeliveryStatus == NotificationStatus.Failed),
                TotalCount = group.Count()
            })
            .Where(x => x.FailedCount > 0)
            .OrderByDescending(x => x.FailedCount)
            .ThenBy(x => x.Key)
            .Take(5)
            .ToListAsync(cancellationToken);
        foreach (var item in topFailingTemplates)
        {
            item.FailureRate = CalculateRate(item.FailedCount, item.TotalCount);
        }

        var failedErrors = await query
            .Where(x => x.DeliveryStatus == NotificationStatus.Failed && x.LastError != null)
            .Select(x => x.LastError!)
            .ToListAsync(cancellationToken);
        var topErrorCategories = failedErrors
            .GroupBy(NormalizeErrorCategory, StringComparer.OrdinalIgnoreCase)
            .Select(group => new NotificationProviderAnalyticsTopFailureDto
            {
                Key = group.Key,
                FailedCount = group.Count(),
                TotalCount = group.Count(),
                FailureRate = 100
            })
            .OrderByDescending(x => x.FailedCount)
            .ThenBy(x => x.Key)
            .Take(5)
            .ToList();

        var providerFailures = await query
            .GroupBy(x => x.Channel)
            .Select(group => new
            {
                Channel = group.Key,
                FailedCount = group.Count(x => x.DeliveryStatus == NotificationStatus.Failed),
                TotalCount = group.Count()
            })
            .Where(x => x.FailedCount > 0)
            .OrderByDescending(x => x.FailedCount)
            .ThenBy(x => x.Channel)
            .Take(5)
            .ToListAsync(cancellationToken);
        var topFailingProviders = providerFailures
            .Select(x => new NotificationProviderAnalyticsTopFailureDto
            {
                Key = x.Channel == NotificationChannel.Email
                    ? emailProviderLabel
                    : x.Channel.ToString(),
                FailedCount = x.FailedCount,
                TotalCount = x.TotalCount,
                FailureRate = CalculateRate(x.FailedCount, x.TotalCount)
            })
            .ToList();

        var channelVolume = await query
            .GroupBy(x => x.Channel)
            .Select(group => new NotificationProviderAnalyticsChannelVolumeDto
            {
                Channel = group.Key,
                TotalCount = group.Count(),
                SentCount = group.Count(x => x.DeliveryStatus == NotificationStatus.ProviderAccepted ||
                    x.DeliveryStatus == NotificationStatus.Delivered),
                FailedCount = group.Count(x => x.DeliveryStatus == NotificationStatus.Failed),
                RetriedCount = group.Count(x => x.TryCount > 1)
            })
            .OrderBy(x => x.Channel)
            .ToListAsync(cancellationToken);
        foreach (var item in channelVolume)
        {
            item.ShareOfTotal = CalculateRate(item.TotalCount, totalDeliveryCount);
        }

        return new NotificationProviderAnalyticsDto
        {
            TenantId = input.TenantId,
            FromUtc = input.FromUtc,
            ToUtc = input.ToUtc,
            TotalNotificationCount = totalNotificationCount,
            TotalDeliveryCount = totalDeliveryCount,
            QueuedCount = queuedCount,
            ProcessingCount = processingCount,
            SentCount = sentCount,
            FailedCount = failedCount,
            RetriedDeliveryCount = retriedDeliveryCount,
            SuccessRate = CalculateRate(sentCount, totalDeliveryCount),
            FailureRate = CalculateRate(failedCount, totalDeliveryCount),
            RetryRate = CalculateRate(retriedDeliveryCount, totalDeliveryCount),
            Last24HoursDeliveryCount = last24HoursDeliveryCount,
            Last24HoursSuccessRate = CalculateRate(last24HoursSentCount, last24HoursDeliveryCount),
            AffectedTemplateCount = affectedTemplateCount,
            AffectedProviderCount = affectedProviderCount,
            StatusMix = statusMix,
            DeliveryVolume = deliveryVolume,
            ChannelVolume = channelVolume,
            TopFailingTemplates = topFailingTemplates,
            TopFailingProviders = topFailingProviders,
            TopErrorCategories = topErrorCategories
        };
    }

    public async Task<NotificationProviderAnalyticsDto> GetCurrentTenantAnalyticsAsync(
        GetCurrentTenantNotificationProviderAnalyticsInput input,
        CancellationToken cancellationToken = default)
    {
        var tenantId = RequireCurrentTenantId();
        var analytics = await GetAnalyticsAsync(
            new GetNotificationProviderAnalyticsInput
            {
                TenantId = tenantId,
                Status = input.Status,
                TemplateName = input.TemplateName,
                Filter = input.Filter,
                FromUtc = input.FromUtc,
                ToUtc = input.ToUtc
            },
            cancellationToken);

        return analytics;
    }

    private static NotificationChannel? ResolveProviderChannel(
        string? provider,
        NotificationChannel? requestedChannel,
        IReadOnlyList<NotificationChannel> effectiveChannels,
        Domain.ProviderConfig.NotificationProviderConfig? emailConfig,
        string emailProviderLabel)
    {
        var normalizedProvider = provider?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedProvider))
        {
            return null;
        }

        var channelMatch = effectiveChannels
            .Where(channel => channel.ToString().Contains(normalizedProvider, StringComparison.OrdinalIgnoreCase))
            .Select(channel => (NotificationChannel?)channel)
            .FirstOrDefault();
        if (channelMatch.HasValue &&
            (!requestedChannel.HasValue || requestedChannel.Value == channelMatch.Value))
        {
            return channelMatch.Value;
        }

        if (effectiveChannels.Contains(NotificationChannel.Email) &&
            (!requestedChannel.HasValue || requestedChannel.Value == NotificationChannel.Email) &&
            ProviderMatches(normalizedProvider, emailConfig, emailProviderLabel))
        {
            return NotificationChannel.Email;
        }

        return null;
    }
}
