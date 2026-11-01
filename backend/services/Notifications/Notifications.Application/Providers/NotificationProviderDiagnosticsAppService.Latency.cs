using Microsoft.EntityFrameworkCore;
using Notifications.Application.Contracts.Providers;
using Notifications.Domain.Notifications;

namespace Notifications.Application.Providers;

public sealed partial class NotificationProviderDiagnosticsAppService
{
    public async Task<NotificationProviderLatencyReportDto> GetLatencyAsync(
        GetNotificationProviderLatencyInput input,
        CancellationToken cancellationToken = default)
    {
        input.TenantId = ResolveTenantScope(input.TenantId);
        using var tenantDatabase = _tenantDatabases.EnterDatabase(await FindHostTargetTenantDatabaseAsync(input.TenantId, cancellationToken));
        using var tenantFilterScope = BeginTenantFilterScope(input.TenantId);

        var fromUtc = input.FromUtc?.ToUniversalTime() ?? DateTime.UtcNow.AddHours(-48);
        var toUtc = input.ToUtc?.ToUniversalTime() ?? DateTime.UtcNow;
        if (toUtc < fromUtc)
        {
            throw new ArgumentException("The latency reporting window must end after it starts.", nameof(input));
        }

        var effectiveChannels = await GetEffectiveChannelsAsync(input.TenantId, cancellationToken);
        if (input.Channel.HasValue && !effectiveChannels.Contains(input.Channel.Value))
        {
            return new NotificationProviderLatencyReportDto
            {
                TenantId = input.TenantId,
                GeneratedAtUtc = DateTime.UtcNow
            };
        }

        var rollups = await (_latencyRollupRepository
            ?? throw new InvalidOperationException("Notification delivery latency rollups are not configured."))
            .GetQueryableAsync();
        var query = rollups
            .AsNoTracking()
            .Where(item =>
                item.TenantId == input.TenantId &&
                item.BucketUtc >= fromUtc &&
                item.BucketUtc <= toUtc &&
                effectiveChannels.Contains(item.Channel));
        if (input.Channel.HasValue)
        {
            query = query.Where(item => item.Channel == input.Channel.Value);
        }

        var buckets = await query
            .OrderBy(item => item.BucketUtc)
            .ThenBy(item => item.Channel)
            .Select(item => new NotificationProviderLatencyBucketDto
            {
                BucketUtc = item.BucketUtc,
                Channel = item.Channel,
                P50Ms = item.P50Ms,
                P95Ms = item.P95Ms,
                P99Ms = item.P99Ms,
                SampleCount = item.SampleCount
            })
            .ToListAsync(cancellationToken);

        return new NotificationProviderLatencyReportDto
        {
            TenantId = input.TenantId,
            GeneratedAtUtc = DateTime.UtcNow,
            Buckets = buckets
        };
    }
}
