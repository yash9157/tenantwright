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
    public Task<NotificationProviderAnalyticsExportDto> ExportCurrentTenantAnalyticsAsync(
        GetCurrentTenantNotificationProviderAnalyticsExportInput input,
        CancellationToken cancellationToken = default)
    {
        var tenantId = RequireCurrentTenantId();

        return ExportAnalyticsAsync(
            new GetNotificationProviderAnalyticsExportInput
            {
                TenantId = tenantId,
                Status = input.Status,
                TemplateName = input.TemplateName,
                Filter = input.Filter,
                FromUtc = input.FromUtc,
                ToUtc = input.ToUtc,
                Format = input.Format
            },
            cancellationToken);
    }

    public async Task<NotificationProviderAnalyticsExportDto> ExportAnalyticsAsync(
        GetNotificationProviderAnalyticsExportInput input,
        CancellationToken cancellationToken = default)
    {
        var analytics = await GetAnalyticsAsync(input, cancellationToken);
        var generatedAtUtc = DateTime.UtcNow;
        var format = input.Format == NotificationProviderAnalyticsExportFormat.Csv
            ? NotificationProviderAnalyticsExportFormat.Csv
            : NotificationProviderAnalyticsExportFormat.Json;
        var filterSummary = CreateFilterSummary(input);
        var extension = format == NotificationProviderAnalyticsExportFormat.Csv ? "csv" : "json";

        return new NotificationProviderAnalyticsExportDto
        {
            Format = format,
            GeneratedAtUtc = generatedAtUtc,
            FileName = $"notification-provider-analytics-{generatedAtUtc:yyyyMMdd-HHmmss}.{extension}",
            ContentType = format == NotificationProviderAnalyticsExportFormat.Csv ? "text/csv" : "application/json",
            FilterSummary = filterSummary,
            Content = format == NotificationProviderAnalyticsExportFormat.Csv
                ? BuildAnalyticsCsv(generatedAtUtc, filterSummary, analytics)
                : BuildAnalyticsJson(generatedAtUtc, filterSummary, analytics)
        };
    }

}
