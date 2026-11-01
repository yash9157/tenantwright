using Microsoft.Extensions.Options;
using Notifications.Application.ProviderConfig.Security;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Notifications.Application.Contracts.Common;
using Notifications.Application.Contracts.Providers;
using Notifications.Application.Emailing;
using Notifications.Domain.Notifications;
using Notifications.Domain.ProviderConfig;
using Notifications.Domain.Shared.Constants;
using Notifications.Domain.Shared.Enums;
using Notifications.EntityFrameworkCore.EntityFrameworkCore;
using Permissions.Application.Contracts.Features;
using Shared.Data.Filtering;
using Shared.Domain;
using Shared.Domain.Repositories;
using Shared.EntityFrameworkCore.TenantDatabases;
using Shared.Permissions.Definitions.Notifications;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;

namespace Notifications.Application.Providers;

[DisableConventionalRegistration]
public sealed partial class NotificationProviderDiagnosticsAppService : INotificationProviderDiagnosticsAppService, ITransientDependency
{
    private const int RecentWindowDays = 7;
    private const int DefaultPageSize = 10;
    private const int MaxPageSize = 25;
    private static readonly JsonSerializerOptions ExportJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly IReadOnlyDictionary<NotificationChannel, string> ChannelFeatures =
        new Dictionary<NotificationChannel, string>
        {
            [NotificationChannel.Email] = NotificationsFeatures.Email,
            [NotificationChannel.Sms] = NotificationsFeatures.Sms,
            [NotificationChannel.Push] = NotificationsFeatures.Push,
            [NotificationChannel.WhatsApp] = NotificationsFeatures.WhatsApp
        };

    private readonly IRepository<NotificationProviderConfig, Guid> _configRepository;
    private readonly IRepository<Notification, Guid> _notificationRepository;
    private readonly IRepository<NotificationDelivery, long> _deliveryRepository;
    private readonly IRepository<NotificationDeliveryLatencyRollup, long>? _latencyRollupRepository;
    private readonly IFeatureCheckerClient _featureChecker;
    private readonly IDataFilter _dataFilter;
    private readonly ICurrentTenant _currentTenant;
    private readonly TenantDatabaseFanOut _tenantDatabases;
    private readonly IOptions<NotificationsProviderSecretProtectionOptions>? _secretProtectionOptions;

    public NotificationProviderDiagnosticsAppService(
        IRepository<NotificationProviderConfig, Guid> configRepository,
        IRepository<Notification, Guid> notificationRepository,
        IRepository<NotificationDelivery, long> deliveryRepository,
        IFeatureCheckerClient featureChecker,
        IDataFilter dataFilter,
        ICurrentTenant currentTenant,
        TenantDatabaseFanOut tenantDatabases,
        IOptions<NotificationsProviderSecretProtectionOptions>? secretProtectionOptions = null,
        IRepository<NotificationDeliveryLatencyRollup, long>? latencyRollupRepository = null)
    {
        _configRepository = configRepository;
        _notificationRepository = notificationRepository;
        _deliveryRepository = deliveryRepository;
        _latencyRollupRepository = latencyRollupRepository;
        _featureChecker = featureChecker;
        _dataFilter = dataFilter;
        _currentTenant = currentTenant;
        _tenantDatabases = tenantDatabases;
        _secretProtectionOptions = secretProtectionOptions;
    }

    private async Task<NotificationProviderConfig?> GetLatestEmailConfigAsync(
        Guid? tenantId,
        CancellationToken cancellationToken)
    {
        var configs = await _configRepository.GetQueryableAsync();
        return await configs
            .AsNoTracking()
            .Where(x => x.Channel == NotificationChannel.Email && x.TenantId == tenantId)
            .OrderByDescending(x => x.LastModificationTime ?? x.CreationTime)
            .ThenByDescending(x => x.CreationTime)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<NotificationChannel>> GetEffectiveChannelsAsync(
        Guid? tenantId,
        CancellationToken cancellationToken)
    {
        var featureValues = await _featureChecker.AreEnabledAsync(
            tenantId,
            ChannelFeatures.Values.ToArray(),
            cancellationToken);

        return ChannelFeatures
            .Where(x => featureValues.TryGetValue(x.Value, out var enabled) && enabled)
            .Select(x => x.Key)
            .OrderBy(x => x)
            .ToArray();
    }

    private async Task<IQueryable<NotificationDelivery>> BuildEnabledDeliveriesQueryAsync(
        Guid? tenantId,
        IReadOnlyList<NotificationChannel> effectiveChannels)
    {
        var notifications = await _notificationRepository.GetQueryableAsync();
        var deliveries = await _deliveryRepository.GetQueryableAsync();

        return deliveries
            .AsNoTracking()
            .Where(delivery => notifications.Any(notification =>
                notification.Id == delivery.NotificationId &&
                effectiveChannels.Contains(notification.Channel) &&
                notification.TenantId == tenantId));
    }

    private async Task<IQueryable<NotificationProviderAnalyticsRow>> BuildAnalyticsQueryAsync(
        GetNotificationProviderAnalyticsInput input,
        IReadOnlyList<NotificationChannel> effectiveChannels,
        bool includeReportingWindow = true)
    {
        var notifications = await _notificationRepository.GetQueryableAsync();
        var deliveries = await _deliveryRepository.GetQueryableAsync();

        var query = deliveries
            .AsNoTracking()
            .Join(
                notifications.AsNoTracking(),
                delivery => delivery.NotificationId,
                notification => notification.Id,
                (delivery, notification) => new NotificationProviderAnalyticsRow
                {
                    NotificationId = notification.Id,
                    TenantId = notification.TenantId,
                    Channel = notification.Channel,
                    TemplateName = notification.TemplateName,
                    Subject = notification.Subject,
                    CreationTime = notification.CreationTime,
                    DeliveryStatus = delivery.Status,
                    TryCount = delivery.TryCount,
                    DeliveryAttemptedAtUtc = delivery.DeliveryAttemptedAtUtc,
                    LastError = delivery.LastError,
                    ProviderMessageId = delivery.ProviderMessageId
                })
            .Where(x => effectiveChannels.Contains(x.Channel) && x.TenantId == input.TenantId);

        if (input.Channel.HasValue)
        {
            query = query.Where(x => x.Channel == input.Channel.Value);
        }

        if (includeReportingWindow && input.FromUtc.HasValue)
        {
            query = query.Where(x => x.CreationTime >= input.FromUtc.Value);
        }

        if (includeReportingWindow && input.ToUtc.HasValue)
        {
            query = query.Where(x => x.CreationTime <= input.ToUtc.Value);
        }

        if (input.Status.HasValue)
        {
            query = query.Where(x => x.DeliveryStatus == input.Status.Value);
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
                (x.Subject != null && x.Subject.ToLower().Contains(normalizedFilter)) ||
                (x.LastError != null && x.LastError.ToLower().Contains(normalizedFilter)) ||
                (x.ProviderMessageId != null && x.ProviderMessageId.ToLower().Contains(normalizedFilter)));
        }

        return query;
    }

    private static NotificationProviderAnalyticsDto CreateEmptyAnalytics(GetNotificationProviderAnalyticsInput input)
    {
        return new NotificationProviderAnalyticsDto
        {
            TenantId = input.TenantId,
            FromUtc = input.FromUtc,
            ToUtc = input.ToUtc
        };
    }

    private static NotificationProviderAnalyticsExportFilterSummaryDto CreateFilterSummary(GetNotificationProviderAnalyticsInput input)
    {
        return new NotificationProviderAnalyticsExportFilterSummaryDto
        {
            TenantId = input.TenantId,
            FromUtc = input.FromUtc,
            ToUtc = input.ToUtc,
            Provider = NormalizeOptionalText(input.Provider),
            Channel = input.Channel,
            Status = input.Status,
            TemplateName = NormalizeOptionalText(input.TemplateName),
            Filter = NormalizeOptionalText(input.Filter)
        };
    }

    private static string BuildAnalyticsJson(
        DateTime generatedAtUtc,
        NotificationProviderAnalyticsExportFilterSummaryDto filterSummary,
        NotificationProviderAnalyticsDto analytics)
    {
        return JsonSerializer.Serialize(
            new
            {
                generatedAtUtc,
                filterSummary,
                kpiTotals = new
                {
                    analytics.TotalNotificationCount,
                    analytics.TotalDeliveryCount,
                    analytics.QueuedCount,
                    analytics.ProcessingCount,
                    analytics.SentCount,
                    analytics.FailedCount,
                    analytics.RetriedDeliveryCount,
                    analytics.SuccessRate,
                    analytics.FailureRate,
                    analytics.RetryRate,
                    analytics.AffectedTemplateCount,
                    analytics.AffectedProviderCount
                },
                statusMix = analytics.StatusMix,
                deliveryVolume = analytics.DeliveryVolume,
                topFailingTemplates = analytics.TopFailingTemplates,
                topFailingProviders = analytics.TopFailingProviders,
                topErrorCategories = analytics.TopErrorCategories
            },
            ExportJsonOptions);
    }

    private static string BuildAnalyticsCsv(
        DateTime generatedAtUtc,
        NotificationProviderAnalyticsExportFilterSummaryDto filterSummary,
        NotificationProviderAnalyticsDto analytics)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Section,Key,Value,Count,Percentage,Total,Sent,Failed,Retried");
        AppendCsvRow(builder, "Metadata", "Generated at UTC", FormatDate(generatedAtUtc));
        AppendCsvRow(builder, "Filter", "Tenant", filterSummary.TenantId?.ToString() ?? "Host default");
        AppendCsvRow(builder, "Filter", "From UTC", FormatDate(filterSummary.FromUtc));
        AppendCsvRow(builder, "Filter", "To UTC", FormatDate(filterSummary.ToUtc));
        AppendCsvRow(builder, "Filter", "Provider", filterSummary.Provider);
        AppendCsvRow(builder, "Filter", "Channel", filterSummary.Channel?.ToString());
        AppendCsvRow(builder, "Filter", "Status", filterSummary.Status?.ToString());
        AppendCsvRow(builder, "Filter", "Template", filterSummary.TemplateName);
        AppendCsvRow(builder, "Filter", "Search", filterSummary.Filter);

        AppendCsvRow(builder, "KPI", "Total notifications", analytics.TotalNotificationCount.ToString(CultureInfo.InvariantCulture));
        AppendCsvRow(builder, "KPI", "Total deliveries", analytics.TotalDeliveryCount.ToString(CultureInfo.InvariantCulture));
        AppendCsvRow(builder, "KPI", "Success rate", analytics.SuccessRate.ToString(CultureInfo.InvariantCulture));
        AppendCsvRow(builder, "KPI", "Failure rate", analytics.FailureRate.ToString(CultureInfo.InvariantCulture));
        AppendCsvRow(builder, "KPI", "Retry rate", analytics.RetryRate.ToString(CultureInfo.InvariantCulture));
        AppendCsvRow(builder, "KPI", "Affected templates", analytics.AffectedTemplateCount.ToString(CultureInfo.InvariantCulture));
        AppendCsvRow(builder, "KPI", "Affected providers", analytics.AffectedProviderCount.ToString(CultureInfo.InvariantCulture));

        foreach (var item in analytics.StatusMix)
        {
            AppendCsvRow(
                builder,
                "Status mix",
                item.Status.ToString(),
                count: item.Count,
                percentage: item.Percentage);
        }

        foreach (var item in analytics.DeliveryVolume)
        {
            AppendCsvRow(
                builder,
                "Delivery volume",
                FormatDate(item.BucketUtc),
                total: item.TotalCount,
                sent: item.SentCount,
                failed: item.FailedCount,
                retried: item.RetriedCount);
        }

        AppendFailureRows(builder, "Top failing templates", analytics.TopFailingTemplates);
        AppendFailureRows(builder, "Top failing providers", analytics.TopFailingProviders);
        AppendFailureRows(builder, "Top error categories", analytics.TopErrorCategories);

        return builder.ToString();
    }

    private Guid? ResolveTenantScope(Guid? requestedTenantId)
    {
        if (!_currentTenant.Id.HasValue)
        {
            return requestedTenantId;
        }

        if (requestedTenantId.HasValue && requestedTenantId.Value != _currentTenant.Id.Value)
        {
            throw new UnauthorizedAccessException("Provider diagnostics must match the authenticated tenant.");
        }

        return _currentTenant.Id.Value;
    }

    /// <summary>
    /// The database host diagnostics for one tenant must read: the tenant's dedicated database when it has one,
    /// otherwise the shared database (null). Tenant callers and host-wide reads keep their scope.
    /// </summary>
    private async Task<TenantDatabase?> FindHostTargetTenantDatabaseAsync(Guid? tenantId, CancellationToken cancellationToken) =>
        !_currentTenant.Id.HasValue && tenantId.HasValue
            ? await _tenantDatabases.FindTenantDatabaseAsync<NotificationsDbContextApp>(tenantId.Value, cancellationToken)
            : null;

    // Lifts the tenant filter for a host read that names one tenant (the queries carry an explicit TenantId
    // predicate). It runs after the tenant's database was entered, so it is a no-op once a dedicated database put the
    // call in tenant scope.
    private IDisposable? BeginTenantFilterScope(Guid? tenantId)
    {
        return !_currentTenant.Id.HasValue && tenantId.HasValue
            ? _dataFilter.Disable<IMultiTenant>()
            : null;
    }

    private Guid RequireCurrentTenantId()
    {
        return _currentTenant.Id
            ?? throw new UnauthorizedAccessException("Tenant notification analytics require an active tenant context.");
    }

    private static void AppendFailureRows(
        StringBuilder builder,
        string section,
        IEnumerable<NotificationProviderAnalyticsTopFailureDto> items)
    {
        foreach (var item in items)
        {
            AppendCsvRow(
                builder,
                section,
                item.Key,
                count: item.FailedCount,
                percentage: item.FailureRate,
                total: item.TotalCount);
        }
    }

    private static void AppendCsvRow(
        StringBuilder builder,
        string section,
        string key,
        string? value = null,
        int? count = null,
        double? percentage = null,
        int? total = null,
        int? sent = null,
        int? failed = null,
        int? retried = null)
    {
        builder
            .Append(EscapeCsv(section)).Append(',')
            .Append(EscapeCsv(key)).Append(',')
            .Append(EscapeCsv(value)).Append(',')
            .Append(count?.ToString(CultureInfo.InvariantCulture) ?? "").Append(',')
            .Append(percentage?.ToString(CultureInfo.InvariantCulture) ?? "").Append(',')
            .Append(total?.ToString(CultureInfo.InvariantCulture) ?? "").Append(',')
            .Append(sent?.ToString(CultureInfo.InvariantCulture) ?? "").Append(',')
            .Append(failed?.ToString(CultureInfo.InvariantCulture) ?? "").Append(',')
            .Append(retried?.ToString(CultureInfo.InvariantCulture) ?? "")
            .AppendLine();
    }

    private static string EscapeCsv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        return value.Contains('"', StringComparison.Ordinal) || value.Contains(',', StringComparison.Ordinal) || value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal)
            ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : value;
    }

    private static string FormatDate(DateTime? value)
    {
        return value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? "";
    }

    private static string? NormalizeOptionalText(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static string ResolveProviderLabel(NotificationProviderConfig? config)
    {
        var host = config != null ? EmailProviderConfigAppService.DeserializeSettings(config.SettingsJson).Host : null;
        return string.IsNullOrWhiteSpace(host) ? "Email provider" : host;
    }

    private static bool ProviderMatches(string? provider, NotificationProviderConfig? config, string providerLabel)
    {
        var normalizedProvider = provider?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedProvider))
        {
            return true;
        }

        var settings = config != null ? EmailProviderConfigAppService.DeserializeSettings(config.SettingsJson) : null;
        return providerLabel.Contains(normalizedProvider, StringComparison.OrdinalIgnoreCase) ||
               (settings != null && settings.FromAddress.Contains(normalizedProvider, StringComparison.OrdinalIgnoreCase)) ||
               (settings?.FromName?.Contains(normalizedProvider, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static string NormalizeErrorCategory(string error)
    {
        var normalized = error.Trim();
        return normalized.Length <= 120 ? normalized : normalized[..120];
    }

    private static double CalculateRate(int value, int total)
    {
        return total <= 0 ? 0 : Math.Round(value * 100d / total, 2, MidpointRounding.AwayFromZero);
    }

    private sealed class NotificationProviderAnalyticsRow
    {
        public Guid NotificationId { get; init; }
        public Guid? TenantId { get; init; }
        public NotificationChannel Channel { get; init; }
        public string TemplateName { get; init; } = "";
        public string? Subject { get; init; }
        public DateTime CreationTime { get; init; }
        public NotificationStatus DeliveryStatus { get; init; }
        public int TryCount { get; init; }
        public DateTime? DeliveryAttemptedAtUtc { get; init; }
        public string? LastError { get; init; }
        public string? ProviderMessageId { get; init; }
    }
}
