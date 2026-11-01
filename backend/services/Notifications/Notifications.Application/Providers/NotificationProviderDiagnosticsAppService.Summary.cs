using Microsoft.EntityFrameworkCore;
using Notifications.Application.Contracts.Providers;
using Notifications.Domain.ProviderConfig;
using Notifications.Domain.Shared.Constants;
using Notifications.Domain.Shared.Enums;

namespace Notifications.Application.Providers;

public sealed partial class NotificationProviderDiagnosticsAppService
{
    public async Task<NotificationProviderDiagnosticsSummaryDto> GetSummaryAsync(
        Guid? tenantId = null,
        CancellationToken cancellationToken = default)
    {
        tenantId = ResolveTenantScope(tenantId);
        using var tenantDatabase = _tenantDatabases.EnterDatabase(await FindHostTargetTenantDatabaseAsync(tenantId, cancellationToken));
        using var tenantFilterScope = BeginTenantFilterScope(tenantId);

        var recentWindowStart = DateTime.UtcNow.AddDays(-RecentWindowDays);
        var effectiveChannels = await GetEffectiveChannelsAsync(tenantId, cancellationToken);
        if (effectiveChannels.Count == 0)
        {
            return new NotificationProviderDiagnosticsSummaryDto
            {
                TenantId = tenantId,
                RecentWindowDays = RecentWindowDays
            };
        }

        var configurationsQuery = await _configRepository.GetQueryableAsync();
        var configurations = await configurationsQuery
            .AsNoTracking()
            .Where(configuration =>
                effectiveChannels.Contains(configuration.Channel) &&
                (configuration.TenantId == tenantId ||
                 (tenantId.HasValue && configuration.TenantId == null)))
            .ToListAsync(cancellationToken);

        var deliveries = await BuildEnabledDeliveriesQueryAsync(tenantId, effectiveChannels);
        var metricsByChannel = (await deliveries
            .GroupBy(delivery => delivery.Notification.Channel)
            .Select(group => new
            {
                Channel = group.Key,
                LatestAttemptAtUtc = group.Max(delivery => delivery.LastTryTimeUtc),
                LatestSuccessAtUtc = group
                    .Where(delivery =>
                        (delivery.Status == NotificationStatus.ProviderAccepted ||
                         delivery.Status == NotificationStatus.Delivered) && delivery.SentTimeUtc.HasValue)
                    .Max(delivery => delivery.SentTimeUtc),
                LatestFailureAtUtc = group
                    .Where(delivery => FailureStatuses.Contains(delivery.Status) && delivery.LastTryTimeUtc.HasValue)
                    .Max(delivery => delivery.LastTryTimeUtc),
                RecentAttemptCount = group.Count(delivery =>
                    delivery.LastTryTimeUtc.HasValue && delivery.LastTryTimeUtc.Value >= recentWindowStart),
                RecentSuccessCount = group.Count(delivery =>
                    (delivery.Status == NotificationStatus.ProviderAccepted ||
                     delivery.Status == NotificationStatus.Delivered) &&
                    delivery.SentTimeUtc.HasValue &&
                    delivery.SentTimeUtc.Value >= recentWindowStart),
                RecentFailureCount = group.Count(delivery =>
                    FailureStatuses.Contains(delivery.Status) &&
                    delivery.LastTryTimeUtc.HasValue &&
                    delivery.LastTryTimeUtc.Value >= recentWindowStart)
            })
            .ToListAsync(cancellationToken))
            .ToDictionary(metric => metric.Channel);
        var latestFailureErrors = (await deliveries
            .Where(delivery => FailureStatuses.Contains(delivery.Status) && delivery.LastTryTimeUtc.HasValue)
            .GroupBy(delivery => delivery.Notification.Channel)
            .Select(group => new
            {
                Channel = group.Key,
                Error = group
                    .OrderByDescending(delivery => delivery.LastTryTimeUtc)
                    .ThenByDescending(delivery => delivery.Id)
                    .Select(delivery => delivery.LastError)
                    .FirstOrDefault()
            })
            .ToListAsync(cancellationToken))
            .ToDictionary(result => result.Channel, result => result.Error);
        var channels = new List<NotificationProviderChannelHealthDto>(effectiveChannels.Count);
        foreach (var channel in effectiveChannels)
        {
            var ownedConfigurations = configurations
                .Where(configuration => configuration.Channel == channel && configuration.TenantId == tenantId)
                .ToList();
            var effectiveDefault = ownedConfigurations.FirstOrDefault(configuration => configuration.IsDefault)
                ?? configurations.FirstOrDefault(configuration =>
                    configuration.Channel == channel &&
                    configuration.TenantId == null &&
                    configuration.IsDefault);
            metricsByChannel.TryGetValue(channel, out var metrics);
            latestFailureErrors.TryGetValue(channel, out var latestFailureError);
            var recentFailureCount = metrics?.RecentFailureCount ?? 0;
            var healthStatus = ResolveHealthStatus(
                ownedConfigurations.Count,
                effectiveDefault,
                recentFailureCount);

            channels.Add(new NotificationProviderChannelHealthDto
            {
                Channel = channel,
                ConfigurationCount = ownedConfigurations.Count,
                DefaultConfigurationId = effectiveDefault?.Id,
                DefaultConfigurationName = effectiveDefault?.Name,
                ProviderType = effectiveDefault?.ProviderType,
                UsesHostFallback = tenantId.HasValue && effectiveDefault?.TenantId == null,
                IsActive = effectiveDefault?.IsActive ?? false,
                ConfigurationUpdatedAtUtc = effectiveDefault == null
                    ? null
                    : effectiveDefault.LastModificationTime ?? effectiveDefault.CreationTime,
                LastTestedAtUtc = effectiveDefault?.LastTestedAtUtc,
                LastTestResult = effectiveDefault?.LastTestResult,
                VerificationState = effectiveDefault?.VerificationState,
                LiveVerifiedAtUtc = effectiveDefault?.LiveVerifiedAtUtc,
                IsLiveVerificationBacked = effectiveDefault?.VerificationState == NotificationProviderVerificationState.LiveVerified &&
                    effectiveDefault.LiveVerifiedAtUtc.HasValue &&
                    !string.IsNullOrWhiteSpace(effectiveDefault.LiveVerificationReference),
                HealthStatus = healthStatus,
                HealthMessage = BuildHealthMessage(channel, healthStatus),
                LatestAttemptAtUtc = metrics?.LatestAttemptAtUtc,
                LatestSuccessAtUtc = metrics?.LatestSuccessAtUtc,
                LatestFailureAtUtc = metrics?.LatestFailureAtUtc,
                LatestFailureError = latestFailureError,
                RecentAttemptCount = metrics?.RecentAttemptCount ?? 0,
                RecentSuccessCount = metrics?.RecentSuccessCount ?? 0,
                RecentFailureCount = recentFailureCount
            });
        }

        return new NotificationProviderDiagnosticsSummaryDto
        {
            TenantId = tenantId,
            RecentWindowDays = RecentWindowDays,
            Channels = channels
        };
    }

    private static readonly NotificationStatus[] FailureStatuses =
    [
        NotificationStatus.Failed,
        NotificationStatus.NeedsReview,
        NotificationStatus.DeadLettered
    ];

    private static bool IsFailureStatus(NotificationStatus status) => FailureStatuses.Contains(status);

    private static NotificationProviderHealthStatus ResolveHealthStatus(
        int ownedConfigurationCount,
        NotificationProviderConfig? effectiveDefault,
        int recentFailureCount)
    {
        if (effectiveDefault == null)
        {
            return ownedConfigurationCount == 0
                ? NotificationProviderHealthStatus.NotConfigured
                : NotificationProviderHealthStatus.Attention;
        }

        if (!effectiveDefault.IsActive)
        {
            return NotificationProviderHealthStatus.Inactive;
        }

        if (recentFailureCount > 0 ||
            string.Equals(effectiveDefault.LastTestResult, "Failed", StringComparison.OrdinalIgnoreCase))
        {
            return NotificationProviderHealthStatus.Attention;
        }

        return effectiveDefault.VerificationState switch
        {
            NotificationProviderVerificationState.LiveVerified when
                effectiveDefault.LiveVerifiedAtUtc.HasValue &&
                !string.IsNullOrWhiteSpace(effectiveDefault.LiveVerificationReference) =>
                NotificationProviderHealthStatus.LiveVerified,
            NotificationProviderVerificationState.TestSendPassed =>
                NotificationProviderHealthStatus.TestSendPassed,
            NotificationProviderVerificationState.VerificationFailed =>
                NotificationProviderHealthStatus.Attention,
            _ => NotificationProviderHealthStatus.Configured
        };
    }

    private static string BuildHealthMessage(
        NotificationChannel channel,
        NotificationProviderHealthStatus status)
    {
        var channelLabel = channel.ToString();
        return status switch
        {
            NotificationProviderHealthStatus.NotConfigured =>
                $"No {channelLabel} provider configuration is available for this scope.",
            NotificationProviderHealthStatus.Inactive =>
                $"The effective {channelLabel} provider configuration is inactive.",
            NotificationProviderHealthStatus.TestSendPassed =>
                $"The effective {channelLabel} configuration passed its latest test send; live verification is still pending.",
            NotificationProviderHealthStatus.LiveVerified =>
                $"The effective {channelLabel} provider has controlled live-verification evidence.",
            NotificationProviderHealthStatus.Attention =>
                $"The effective {channelLabel} provider configuration or recent delivery activity needs review.",
            _ =>
                $"The effective {channelLabel} provider is configured but has not passed a test send."
        };
    }
}
