using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Notifications.Application.Contracts.Providers;
using Notifications.Application.Delivery;
using Notifications.Application.ProviderConfig.Security;
using Notifications.Application.Providers;
using Notifications.Domain.Notifications;
using Notifications.Domain.ProviderConfig;
using Notifications.Domain.Shared.Enums;
using Permissions.Application.Contracts.Features;
using Shared.Domain.Repositories;
using Xunit;

namespace Notifications.Tests;

public class NotificationDeliveryAnalyticsTests
{
    [Fact]
    public async Task Analytics_returns_tenant_scoped_channel_volume_and_last_24_hour_success_rate()
    {
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        await using var dataContext = NotificationsEfCoreTestHarness.BuildDbContext(
            nameof(Analytics_returns_tenant_scoped_channel_volume_and_last_24_hour_success_rate),
            tenantId: null,
            multiTenantFilterEnabled: false);

        var completedAtUtc = DateTime.UtcNow.AddMinutes(-10);
        dataContext.Notifications.AddRange(
            NewNotification(tenantId, NotificationChannel.Email),
            NewNotification(tenantId, NotificationChannel.Email),
            NewNotification(tenantId, NotificationChannel.Sms),
            NewNotification(otherTenantId, NotificationChannel.Push));
        await dataContext.SaveChangesAsync();

        var notifications = await dataContext.Notifications.OrderBy(item => item.Channel).ToListAsync();
        dataContext.NotificationDeliveries.AddRange(
            NewCompletedDelivery(1, tenantId, notifications[0].Id, completedAtUtc, 100),
            NewCompletedDelivery(2, tenantId, notifications[1].Id, completedAtUtc, 120),
            NewFailedDelivery(3, tenantId, notifications[2].Id, completedAtUtc),
            NewCompletedDelivery(4, otherTenantId, notifications[3].Id, completedAtUtc, 90));
        await dataContext.SaveChangesAsync();

        var service = CreateDiagnostics(dataContext);
        var report = await service.GetAnalyticsAsync(new GetNotificationProviderAnalyticsInput { TenantId = tenantId });

        Assert.Equal(3, report.TotalDeliveryCount);
        Assert.Equal(3, report.Last24HoursDeliveryCount);
        Assert.Equal(66.67, report.Last24HoursSuccessRate);
        Assert.Collection(
            report.ChannelVolume,
            email =>
            {
                Assert.Equal(NotificationChannel.Email, email.Channel);
                Assert.Equal(2, email.TotalCount);
                Assert.Equal(66.67, email.ShareOfTotal);
            },
            sms =>
            {
                Assert.Equal(NotificationChannel.Sms, sms.Channel);
                Assert.Equal(1, sms.TotalCount);
                Assert.Equal(33.33, sms.ShareOfTotal);
            });
    }

    [Fact]
    public async Task Analytics_last_24_hour_context_ignores_only_the_reporting_window()
    {
        var tenantId = Guid.NewGuid();
        await using var dataContext = NotificationsEfCoreTestHarness.BuildDbContext(
            nameof(Analytics_last_24_hour_context_ignores_only_the_reporting_window),
            tenantId: null,
            multiTenantFilterEnabled: false);

        var notification = NewNotification(tenantId, NotificationChannel.Email);
        dataContext.Notifications.Add(notification);
        await dataContext.SaveChangesAsync();
        dataContext.NotificationDeliveries.Add(
            NewCompletedDelivery(1, tenantId, notification.Id, DateTime.UtcNow.AddMinutes(-5), 125));
        await dataContext.SaveChangesAsync();

        var service = CreateDiagnostics(dataContext);
        var report = await service.GetAnalyticsAsync(new GetNotificationProviderAnalyticsInput
        {
            TenantId = tenantId,
            Channel = NotificationChannel.Email,
            FromUtc = DateTime.UtcNow.AddDays(-30),
            ToUtc = DateTime.UtcNow.AddDays(-29)
        });

        Assert.Equal(0, report.TotalDeliveryCount);
        Assert.Equal(1, report.Last24HoursDeliveryCount);
        Assert.Equal(100, report.Last24HoursSuccessRate);
    }

    [Fact]
    public async Task Latency_rollup_uses_only_attempt_to_completion_and_replaces_the_same_tenant_bucket()
    {
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        await using var dataContext = NotificationsEfCoreTestHarness.BuildDbContext(
            nameof(Latency_rollup_uses_only_attempt_to_completion_and_replaces_the_same_tenant_bucket),
            tenantId: null,
            multiTenantFilterEnabled: false);

        var completedAtUtc = StartOfCurrentHourUtc().AddHours(-1).AddMinutes(10);
        var tenantNotification = NewNotification(tenantId, NotificationChannel.Email);
        var otherNotification = NewNotification(otherTenantId, NotificationChannel.Email);
        dataContext.Notifications.AddRange(tenantNotification, otherNotification);
        await dataContext.SaveChangesAsync();
        dataContext.NotificationDeliveries.AddRange(
            NewCompletedDelivery(1, tenantId, tenantNotification.Id, completedAtUtc, 100),
            NewCompletedDelivery(2, tenantId, tenantNotification.Id, completedAtUtc.AddMinutes(2), 300),
            NewCompletedDelivery(3, otherTenantId, otherNotification.Id, completedAtUtc, 900));
        await dataContext.SaveChangesAsync();

        var processor = CreateRollupProcessor(dataContext, maxBucketsPerRun: 8);

        var firstBatch = await processor.RollupBatchAsync();
        var secondBatch = await processor.RollupBatchAsync();

        var rollups = dataContext.NotificationDeliveryLatencyRollups
            .OrderBy(item => item.TenantId)
            .ToList();
        Assert.Equal(2, firstBatch.ProcessedBucketCount);
        Assert.Equal(2, secondBatch.ProcessedBucketCount);
        Assert.False(secondBatch.HasMoreUnrolledBuckets);
        Assert.Equal(2, rollups.Count);
        var tenantRollup = Assert.Single(rollups.Where(item => item.TenantId == tenantId));
        Assert.Equal(2, tenantRollup.SampleCount);
        Assert.Equal(100, tenantRollup.P50Ms);
        Assert.Equal(300, tenantRollup.P95Ms);
        Assert.Equal(300, tenantRollup.P99Ms);
        Assert.Single(rollups.Where(item => item.TenantId == otherTenantId));
    }

    [Fact]
    public async Task Latency_rollup_drains_unrolled_buckets_without_starving_newer_hours()
    {
        var tenantId = Guid.NewGuid();
        await using var dataContext = NotificationsEfCoreTestHarness.BuildDbContext(
            nameof(Latency_rollup_drains_unrolled_buckets_without_starving_newer_hours),
            tenantId: null,
            multiTenantFilterEnabled: false);

        var notifications = new[]
        {
            NewNotification(tenantId, NotificationChannel.Email),
            NewNotification(tenantId, NotificationChannel.Email),
            NewNotification(tenantId, NotificationChannel.Email)
        };
        dataContext.Notifications.AddRange(notifications);
        await dataContext.SaveChangesAsync();
        var completedHour = StartOfCurrentHourUtc();
        dataContext.NotificationDeliveries.AddRange(
            NewCompletedDelivery(1, tenantId, notifications[0].Id, completedHour.AddHours(-3).AddMinutes(15), 100),
            NewCompletedDelivery(2, tenantId, notifications[1].Id, completedHour.AddHours(-2).AddMinutes(15), 200),
            NewCompletedDelivery(3, tenantId, notifications[2].Id, completedHour.AddHours(-1).AddMinutes(15), 300));
        await dataContext.SaveChangesAsync();

        var processor = CreateRollupProcessor(dataContext, maxBucketsPerRun: 1);

        var firstBatch = await processor.RollupBatchAsync();
        var secondBatch = await processor.RollupBatchAsync();
        var thirdBatch = await processor.RollupBatchAsync();

        Assert.Equal(1, firstBatch.ProcessedBucketCount);
        Assert.True(firstBatch.HasMoreUnrolledBuckets);
        Assert.Equal(1, secondBatch.ProcessedBucketCount);
        Assert.True(secondBatch.HasMoreUnrolledBuckets);
        Assert.Equal(1, thirdBatch.ProcessedBucketCount);
        Assert.False(thirdBatch.HasMoreUnrolledBuckets);
        Assert.Equal(
            3,
            await dataContext.NotificationDeliveryLatencyRollups
                .CountAsync(item => item.TenantId == tenantId && item.Channel == NotificationChannel.Email));
    }

    [Fact]
    public async Task Latency_rollup_excludes_active_hours_and_invalid_completion_intervals()
    {
        var tenantId = Guid.NewGuid();
        await using var dataContext = NotificationsEfCoreTestHarness.BuildDbContext(
            nameof(Latency_rollup_excludes_active_hours_and_invalid_completion_intervals),
            tenantId: null,
            multiTenantFilterEnabled: false);

        var notifications = new[]
        {
            NewNotification(tenantId, NotificationChannel.Email),
            NewNotification(tenantId, NotificationChannel.Email),
            NewNotification(tenantId, NotificationChannel.Email)
        };
        dataContext.Notifications.AddRange(notifications);
        await dataContext.SaveChangesAsync();
        var completedHour = StartOfCurrentHourUtc();
        dataContext.NotificationDeliveries.AddRange(
            NewCompletedDelivery(1, tenantId, notifications[0].Id, completedHour.AddHours(-1).AddMinutes(15), 100),
            NewCompletedDelivery(2, tenantId, notifications[1].Id, DateTime.UtcNow.AddMinutes(-1), 200),
            NewCompletedDelivery(3, tenantId, notifications[2].Id, completedHour.AddHours(-1).AddMinutes(30), -100));
        await dataContext.SaveChangesAsync();

        var processor = CreateRollupProcessor(dataContext, maxBucketsPerRun: 8);
        var batch = await processor.RollupBatchAsync();

        var rollup = Assert.Single(dataContext.NotificationDeliveryLatencyRollups);
        Assert.Equal(1, batch.ProcessedBucketCount);
        Assert.Equal(1, rollup.SampleCount);
        Assert.Equal(100, rollup.P50Ms);
    }

    [Fact]
    public void Latency_rollup_rejects_invalid_percentile_ordering_and_template_management_contract_exposes_authoritative_history()
    {
        var rollup = new NotificationDeliveryLatencyRollup(
            id: 1,
            tenantId: Guid.NewGuid(),
            channel: NotificationChannel.Email,
            bucketUtc: DateTime.UtcNow,
            sampleCount: 1,
            p50Ms: 10,
            p95Ms: 20,
            p99Ms: 30,
            calculatedAtUtc: DateTime.UtcNow);

        Assert.Throws<ArgumentOutOfRangeException>(() => rollup.Replace(1, 20, 10, 30, DateTime.UtcNow));
        Assert.NotNull(typeof(Notifications.Application.Contracts.Templates.NotificationTemplateManageDetailDto)
            .GetProperty("PublishedBodyTemplate"));
        Assert.NotNull(typeof(Notifications.Application.Contracts.Templates.NotificationTemplateRevisionListItemDto)
            .GetProperty("CreatorId"));
    }

    private static NotificationProviderDiagnosticsAppService CreateDiagnostics(
        Notifications.EntityFrameworkCore.EntityFrameworkCore.NotificationsDbContextApp dataContext)
    {
        return new NotificationProviderDiagnosticsAppService(
            new EfTestRepository<NotificationProviderConfig, Guid>(dataContext),
            new EfTestRepository<Notification, Guid>(dataContext),
            new EfTestRepository<NotificationDelivery, long>(dataContext),
            new AllChannelsFeatureChecker(),
            new ConfigurableDataFilter(enabled: false),
            new TestCurrentTenant(null),
            SharedOnlyTenantDatabases.FanOut(),
            Options.Create(new NotificationsProviderSecretProtectionOptions()),
            new EfTestRepository<NotificationDeliveryLatencyRollup, long>(dataContext));
    }

    private static NotificationDeliveryLatencyRollupProcessor CreateRollupProcessor(
        Notifications.EntityFrameworkCore.EntityFrameworkCore.NotificationsDbContextApp dataContext,
        int maxBucketsPerRun)
    {
        return new NotificationDeliveryLatencyRollupProcessor(
            new ImmediateUnitOfWorkExecutor(),
            NotificationsEfCoreTestHarness.AsDbContextProvider(dataContext),
            Options.Create(new NotificationsDeliveryLatencyRollupOptions
            {
                LookbackHours = 4,
                MaxBucketsPerRun = maxBucketsPerRun
            }));
    }

    private static Notification NewNotification(Guid tenantId, NotificationChannel channel)
    {
        return new Notification(
            Guid.NewGuid(),
            tenantId,
            Guid.NewGuid(),
            channel,
            "Notifications.Analytics",
            "{}");
    }

    private static NotificationDelivery NewCompletedDelivery(
        long id,
        Guid tenantId,
        Guid notificationId,
        DateTime completedAtUtc,
        int latencyMilliseconds)
    {
        var delivery = new NotificationDelivery(id, tenantId, notificationId, id);
        delivery.MarkProcessing(
            completedAtUtc.AddMilliseconds(-latencyMilliseconds),
            completedAtUtc.AddMinutes(1),
            Guid.NewGuid());
        delivery.MarkProviderAccepted(completedAtUtc, "provider-message");
        return delivery;
    }

    private static NotificationDelivery NewFailedDelivery(
        long id,
        Guid tenantId,
        Guid notificationId,
        DateTime attemptedAtUtc)
    {
        var delivery = new NotificationDelivery(id, tenantId, notificationId, id);
        delivery.MarkProcessing(attemptedAtUtc, attemptedAtUtc.AddMinutes(1), Guid.NewGuid());
        delivery.MarkFailed("Provider failure", attemptedAtUtc.AddMinutes(5), attemptedAtUtc.AddSeconds(1));
        return delivery;
    }

    private static DateTime StartOfCurrentHourUtc()
    {
        var now = DateTime.UtcNow;
        return new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc);
    }

    private sealed class AllChannelsFeatureChecker : IFeatureCheckerClient
    {
        public Task<bool> IsEnabledAsync(Guid? tenantId, string featureName, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<Dictionary<string, bool>> AreEnabledAsync(
            Guid? tenantId,
            IReadOnlyCollection<string> featureNames,
            CancellationToken cancellationToken = default)
            => Task.FromResult(featureNames.ToDictionary(feature => feature, _ => true, StringComparer.Ordinal));
    }
}
