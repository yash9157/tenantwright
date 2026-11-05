using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Notifications.Application.Contracts.Delivery;
using Notifications.Application.Delivery;
using Notifications.Domain.Notifications;
using Permissions.Application.Contracts.Features;
using Shared.BackgroundTasks.Workers;
using Shared.EventContracts;
using Xunit;

namespace Notifications.Tests;

public class NotificationScheduledSendTests
{
    [Fact]
    public async Task Scheduled_send_persists_notification_and_delivery_due_timestamps_and_remains_request_idempotent()
    {
        var tenantId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var scheduledForUtc = DateTime.UtcNow.AddHours(2);
        await using var dbContext = NotificationsEfCoreTestHarness.BuildDbContext(
            nameof(Scheduled_send_persists_notification_and_delivery_due_timestamps_and_remains_request_idempotent),
            tenantId: null,
            multiTenantFilterEnabled: true);
        var scheduler = CreateScheduler(dbContext);
        var request = CreateRequest(tenantId, requestId);

        var first = await scheduler.ScheduleAtAsync(request, scheduledForUtc);
        await dbContext.SaveChangesAsync();
        var duplicate = await scheduler.ScheduleAtAsync(request, scheduledForUtc);

        Assert.True(first.Accepted);
        Assert.False(first.IsDuplicate);
        Assert.Equal(scheduledForUtc, first.ScheduledForUtc);
        Assert.True(duplicate.IsDuplicate);
        Assert.Equal(first.NotificationId, duplicate.NotificationId);

        var notification = await dbContext.Notifications.IgnoreQueryFilters().SingleAsync();
        var delivery = await dbContext.NotificationDeliveries.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(scheduledForUtc, notification.ScheduledForUtc);
        Assert.Equal(scheduledForUtc, delivery.NotBeforeUtc);
        Assert.Equal(scheduledForUtc, delivery.NextAttemptTimeUtc);
    }

    [Fact]
    public async Task Scheduled_send_rejects_past_non_utc_and_beyond_365_day_horizon()
    {
        await using var dbContext = NotificationsEfCoreTestHarness.BuildDbContext(
            nameof(Scheduled_send_rejects_past_non_utc_and_beyond_365_day_horizon),
            tenantId: null,
            multiTenantFilterEnabled: true);
        var scheduler = CreateScheduler(dbContext);

        var pastException = await Assert.ThrowsAsync<Shared.SharedValidationException>(() =>
            scheduler.ScheduleAtAsync(CreateRequest(Guid.NewGuid(), Guid.NewGuid()), DateTime.UtcNow.AddMinutes(-1)));
        Assert.Equal("notification_schedule_must_be_future", pastException.Code);

        var nonUtcException = await Assert.ThrowsAsync<Shared.SharedValidationException>(() =>
            scheduler.ScheduleAtAsync(
                CreateRequest(Guid.NewGuid(), Guid.NewGuid()),
                DateTime.SpecifyKind(DateTime.UtcNow.AddHours(1), DateTimeKind.Unspecified)));
        Assert.Equal("notification_schedule_utc_required", nonUtcException.Code);

        var horizonException = await Assert.ThrowsAsync<Shared.SharedValidationException>(() =>
            scheduler.ScheduleAtAsync(CreateRequest(Guid.NewGuid(), Guid.NewGuid()), DateTime.UtcNow.AddDays(366)));
        Assert.Equal("notification_schedule_horizon_exceeded", horizonException.Code);

        Assert.Empty(dbContext.Notifications.IgnoreQueryFilters());
        Assert.Empty(dbContext.NotificationDeliveries.IgnoreQueryFilters());
    }

    private static NotificationDeliveryScheduler CreateScheduler(
        Notifications.EntityFrameworkCore.EntityFrameworkCore.NotificationsDbContextApp dbContext)
    {
        return new NotificationDeliveryScheduler(
            new NoopUnitOfWorkManager(),
            new ImmediateUnitOfWorkExecutor(),
            new EfTestRepository<Notification, Guid>(dbContext),
            new EfTestRepository<NotificationRecipient, long>(dbContext),
            new EfTestRepository<NotificationDelivery, long>(dbContext),
            Options.Create(new NotificationsChannelsOptions { EmailEnabled = true }),
            Options.Create(new NotificationsDeliveryOptions { MaxScheduleHorizonDays = 365 }),
            new AlwaysEnabledFeatureChecker(),
            new EnabledPreferenceClient(),
            new NoopWakeSignal(),
            new PassThroughRateLimiter(),
            new PassThroughAudienceResolver(),
            new NeverSuppressedTenantNotificationChecker(),
            new NoopNotificationDigestPlanner(),
            NullLogger<NotificationDeliveryScheduler>.Instance);
    }

    private static NotificationChannelRequestTenant CreateRequest(Guid tenantId, Guid requestId)
    {
        return new NotificationChannelRequestTenant
        {
            RequestId = requestId,
            TenantId = tenantId,
            SourceService = "notifications-tests",
            Channel = NotificationSendChannel.Email,
            TemplateName = "Identity.SecurityRiskBlockedAlert",
            Recipients =
            [
                new NotificationRecipientEto
                {
                    RecipientType = NotificationSendRecipientType.EmailAddress,
                    RecipientValue = "operator@test.local"
                }
            ]
        };
    }

    private sealed class AlwaysEnabledFeatureChecker : IFeatureCheckerClient
    {
        public Task<bool> IsEnabledAsync(Guid? tenantId, string featureName, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<Dictionary<string, bool>> AreEnabledAsync(
            Guid? tenantId,
            IReadOnlyCollection<string> featureNames,
            CancellationToken cancellationToken = default)
            => Task.FromResult(featureNames.ToDictionary(x => x, _ => true, StringComparer.Ordinal));
    }

    private sealed class EnabledPreferenceClient : IIdentityNotificationPreferenceClient
    {
        public Task<bool> IsNotificationEnabledAsync(
            Guid? tenantId,
            Guid userId,
            string preferenceKey,
            CancellationToken cancellationToken = default)
            => Task.FromResult(true);
    }

    private sealed class NoopWakeSignal : IBackgroundWorkerWakeSignal
    {
        public Task SignalAsync(
            string key,
            BackgroundWorkerWakeOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<BackgroundWorkerWakeResult> WaitAsync(
            string key,
            BackgroundWorkerWakeOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(BackgroundWorkerWakeResult.SafetyPollTimeout);
    }
}
