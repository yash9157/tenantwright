using Microsoft.EntityFrameworkCore;
using Notifications.Application.Contracts.Delivery;
using Notifications.Application.Contracts.Replay;
using Notifications.Application.Replay;
using Notifications.Domain.Notifications;
using Notifications.Domain.Shared.Enums;
using Shared.Domain.Repositories;
using Shared.EventContracts;
using Xunit;

namespace Notifications.Tests;

public class NotificationReplaySchedulingTests
{
    [Fact]
    public async Task ReplayAsync_should_forward_the_requested_utc_time_to_the_durable_scheduler()
    {
        var tenantId = Guid.CreateVersion7();
        var notificationId = Guid.CreateVersion7();
        var scheduledForUtc = DateTime.UtcNow.AddHours(1);
        await using var dbContext = NotificationsEfCoreTestHarness.BuildDbContext(
            nameof(ReplayAsync_should_forward_the_requested_utc_time_to_the_durable_scheduler),
            tenantId);

        dbContext.Notifications.Add(new Notification(
            notificationId,
            tenantId,
            Guid.CreateVersion7(),
            NotificationChannel.Email,
            "Identity.SecurityRiskBlockedAlert",
            "{}",
            "Security alert"));
        dbContext.NotificationRecipients.Add(new NotificationRecipient(
            1,
            tenantId,
            notificationId,
            NotificationRecipientType.EmailAddress,
            "admin@example.test"));
        await dbContext.SaveChangesAsync();

        var scheduler = new CapturingScheduler();
        var appService = new NotificationReplayAppService(new EfTestRepository<Notification, Guid>(dbContext), new EfTestRepository<NotificationRecipient, long>(dbContext), scheduler, new ConfigurableDataFilter(true), new TestCurrentTenant(null), SharedOnlyTenantDatabases.Locator());

        var operationId = Guid.CreateVersion7();
        var result = await appService.ReplayAsync(new ReplayNotificationInput
        {
            NotificationId = notificationId,
            OperationId = operationId,
            ScheduledForUtc = scheduledForUtc
        });

        Assert.Equal(operationId, scheduler.RequestId);

        Assert.Equal(scheduledForUtc, scheduler.NotBeforeUtc);
        Assert.Equal(typeof(NotificationSendRequestedTenantEto), scheduler.LastContractType);
        Assert.Equal(scheduledForUtc, result.ScheduledForUtc);
        Assert.Equal(notificationId, result.OriginalNotificationId);
    }

    [Fact]
    public async Task ReplayAsync_should_use_the_host_delayed_scheduler_for_host_notifications()
    {
        var notificationId = Guid.CreateVersion7();
        var scheduledForUtc = DateTime.UtcNow.AddHours(1);
        await using var dbContext = NotificationsEfCoreTestHarness.BuildDbContext(
            nameof(ReplayAsync_should_use_the_host_delayed_scheduler_for_host_notifications),
            tenantId: null);

        dbContext.Notifications.Add(new Notification(
            notificationId,
            tenantId: null,
            Guid.CreateVersion7(),
            NotificationChannel.Email,
            "Identity.SecurityRiskBlockedAlert",
            "{}",
            "Security alert"));
        dbContext.NotificationRecipients.Add(new NotificationRecipient(
            1,
            tenantId: null,
            notificationId,
            NotificationRecipientType.EmailAddress,
            "operator@example.test"));
        await dbContext.SaveChangesAsync();

        var scheduler = new CapturingScheduler();
        var appService = new NotificationReplayAppService(new EfTestRepository<Notification, Guid>(dbContext), new EfTestRepository<NotificationRecipient, long>(dbContext), scheduler, new ConfigurableDataFilter(true), new TestCurrentTenant(null), SharedOnlyTenantDatabases.Locator());

        var operationId = Guid.CreateVersion7();
        var result = await appService.ReplayAsync(new ReplayNotificationInput
        {
            NotificationId = notificationId,
            OperationId = operationId,
            ScheduledForUtc = scheduledForUtc
        });

        Assert.Equal(operationId, scheduler.RequestId);

        Assert.Equal(scheduledForUtc, scheduler.NotBeforeUtc);
        Assert.Equal(typeof(NotificationSendRequestedHostEto), scheduler.LastContractType);
        Assert.Equal(scheduledForUtc, result.ScheduledForUtc);
    }

    [Fact]
    public async Task ReplayAsync_empty_notification_id_should_return_a_coded_validation_error()
    {
        await using var dbContext = NotificationsEfCoreTestHarness.BuildDbContext(
            nameof(ReplayAsync_empty_notification_id_should_return_a_coded_validation_error),
            tenantId: Guid.CreateVersion7());
        var appService = new NotificationReplayAppService(new EfTestRepository<Notification, Guid>(dbContext), new EfTestRepository<NotificationRecipient, long>(dbContext), new CapturingScheduler(), new ConfigurableDataFilter(true), new TestCurrentTenant(null), SharedOnlyTenantDatabases.Locator());

        var exception = await Assert.ThrowsAsync<Shared.SharedValidationException>(() =>
            appService.ReplayAsync(new ReplayNotificationInput()));

        Assert.Equal("notification_id_required", exception.Code);
        Assert.Equal("NotificationId is required.", exception.Message);
    }

    [Fact]
    public async Task ReplayAsync_empty_operation_id_should_return_a_coded_validation_error()
    {
        var notificationId = Guid.CreateVersion7();
        await using var dbContext = NotificationsEfCoreTestHarness.BuildDbContext(
            nameof(ReplayAsync_empty_operation_id_should_return_a_coded_validation_error),
            tenantId: Guid.CreateVersion7());
        var appService = new NotificationReplayAppService(new EfTestRepository<Notification, Guid>(dbContext), new EfTestRepository<NotificationRecipient, long>(dbContext), new CapturingScheduler(), new ConfigurableDataFilter(true), new TestCurrentTenant(null), SharedOnlyTenantDatabases.Locator());

        var exception = await Assert.ThrowsAsync<Shared.SharedValidationException>(() =>
            appService.ReplayAsync(new ReplayNotificationInput { NotificationId = notificationId }));

        Assert.Equal("notification_replay_operation_id_required", exception.Code);
        Assert.Equal("OperationId is required.", exception.Message);
    }

    [Fact]
    public async Task ReplayAsync_missing_notification_should_return_a_coded_not_found_error()
    {
        var tenantId = Guid.CreateVersion7();
        var notificationId = Guid.CreateVersion7();
        await using var dbContext = NotificationsEfCoreTestHarness.BuildDbContext(
            nameof(ReplayAsync_missing_notification_should_return_a_coded_not_found_error),
            tenantId);
        var appService = new NotificationReplayAppService(new EfTestRepository<Notification, Guid>(dbContext), new EfTestRepository<NotificationRecipient, long>(dbContext), new CapturingScheduler(), new ConfigurableDataFilter(true), new TestCurrentTenant(null), SharedOnlyTenantDatabases.Locator());

        var exception = await Assert.ThrowsAsync<Shared.Domain.Entities.EntityNotFoundException>(() =>
            appService.ReplayAsync(new ReplayNotificationInput { NotificationId = notificationId, OperationId = Guid.CreateVersion7() }));

        Assert.Equal("notification_not_found", exception.Code);
        Assert.Equal($"Notification '{notificationId}' was not found.", exception.Message);
    }

    [Fact]
    public async Task ReplayAsync_notification_without_recipients_should_return_a_coded_conflict_error()
    {
        var tenantId = Guid.CreateVersion7();
        var notificationId = Guid.CreateVersion7();
        await using var dbContext = NotificationsEfCoreTestHarness.BuildDbContext(
            nameof(ReplayAsync_notification_without_recipients_should_return_a_coded_conflict_error),
            tenantId);
        dbContext.Notifications.Add(new Notification(
            notificationId,
            tenantId,
            Guid.CreateVersion7(),
            NotificationChannel.Email,
            "Identity.SecurityRiskBlockedAlert",
            "{}",
            "Security alert"));
        await dbContext.SaveChangesAsync();

        var appService = new NotificationReplayAppService(new EfTestRepository<Notification, Guid>(dbContext), new EfTestRepository<NotificationRecipient, long>(dbContext), new CapturingScheduler(), new ConfigurableDataFilter(true), new TestCurrentTenant(null), SharedOnlyTenantDatabases.Locator());

        var exception = await Assert.ThrowsAsync<Shared.SharedConflictException>(() =>
            appService.ReplayAsync(new ReplayNotificationInput { NotificationId = notificationId, OperationId = Guid.CreateVersion7() }));

        Assert.Equal("notification_no_replay_recipients", exception.Code);
        Assert.Equal("The selected notification has no resolved recipients to replay.", exception.Message);
    }

    [Fact]
    public async Task ReplayAsync_host_operator_should_disable_the_tenant_filter_for_entity_lookup()
    {
        var tenantId = Guid.CreateVersion7();
        var notificationId = Guid.CreateVersion7();
        await using var dbContext = NotificationsEfCoreTestHarness.BuildDbContext(
            nameof(ReplayAsync_host_operator_should_disable_the_tenant_filter_for_entity_lookup),
            tenantId);
        dbContext.Notifications.Add(new Notification(
            notificationId,
            tenantId,
            Guid.CreateVersion7(),
            NotificationChannel.Email,
            "Identity.SecurityRiskBlockedAlert",
            "{}"));
        dbContext.NotificationRecipients.Add(new NotificationRecipient(
            1,
            tenantId,
            notificationId,
            NotificationRecipientType.EmailAddress,
            "operator@example.test"));
        await dbContext.SaveChangesAsync();

        var dataFilter = new ConfigurableDataFilter(enabled: true);
        var service = new NotificationReplayAppService(
            new EfTestRepository<Notification, Guid>(dbContext),
            new EfTestRepository<NotificationRecipient, long>(dbContext),
            new CapturingScheduler(),
            dataFilter,
            new TestCurrentTenant(null), SharedOnlyTenantDatabases.Locator());

        await service.ReplayAsync(new ReplayNotificationInput
        {
            NotificationId = notificationId,
            OperationId = Guid.CreateVersion7(),
            ScheduledForUtc = DateTime.UtcNow.AddMinutes(5)
        });

        Assert.Equal(1, dataFilter.DisableCount);
    }

    [Fact]
    public async Task ReplayAsync_tenant_operator_should_reject_a_foreign_tenant_notification()
    {
        var currentTenantId = Guid.CreateVersion7();
        var foreignTenantId = Guid.CreateVersion7();
        var notificationId = Guid.CreateVersion7();
        await using var dbContext = NotificationsEfCoreTestHarness.BuildDbContext(
            nameof(ReplayAsync_tenant_operator_should_reject_a_foreign_tenant_notification),
            tenantId: null,
            multiTenantFilterEnabled: false);
        dbContext.Notifications.Add(new Notification(
            notificationId,
            foreignTenantId,
            Guid.CreateVersion7(),
            NotificationChannel.Email,
            "Identity.SecurityRiskBlockedAlert",
            "{}"));
        await dbContext.SaveChangesAsync();

        var service = new NotificationReplayAppService(
            new EfTestRepository<Notification, Guid>(dbContext),
            new EfTestRepository<NotificationRecipient, long>(dbContext),
            new CapturingScheduler(),
            new ConfigurableDataFilter(enabled: true),
            new TestCurrentTenant(currentTenantId), SharedOnlyTenantDatabases.Locator());

        var exception = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.ReplayAsync(new ReplayNotificationInput
            {
                NotificationId = notificationId,
                OperationId = Guid.CreateVersion7()
            }));

        Assert.Equal("Cross-tenant notification replay is not allowed.", exception.Message);
    }

    private sealed class CapturingScheduler : INotificationDeliveryScheduler
    {
        public DateTime? NotBeforeUtc { get; private set; }
        public Type? LastContractType { get; private set; }
        public Guid? RequestId { get; private set; }

        public Task<NotificationScheduleResult> ScheduleAsync(
            NotificationSendRequestedTenantEto input,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("A scheduled replay must use ScheduleAtAsync.");
        }

        public Task<NotificationScheduleResult> ScheduleAsync(
            NotificationSendRequestedHostEto input,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("A scheduled replay must use ScheduleAtAsync.");
        }

        public Task<NotificationScheduleResult> ScheduleAtAsync(
            NotificationSendRequestedTenantEto input,
            DateTime notBeforeUtc,
            CancellationToken cancellationToken = default)
        {
            LastContractType = typeof(NotificationSendRequestedTenantEto);
            return CaptureDelayedAsync(input.RequestId, input.Recipients.Count, notBeforeUtc);
        }

        public Task<NotificationScheduleResult> ScheduleAtAsync(
            NotificationSendRequestedHostEto input,
            DateTime notBeforeUtc,
            CancellationToken cancellationToken = default)
        {
            LastContractType = typeof(NotificationSendRequestedHostEto);
            return CaptureDelayedAsync(input.RequestId, input.Recipients.Count, notBeforeUtc);
        }

        private Task<NotificationScheduleResult> CaptureDelayedAsync(
            Guid requestId,
            int recipientCount,
            DateTime notBeforeUtc)
        {
            RequestId = requestId;
            NotBeforeUtc = notBeforeUtc;
            return Task.FromResult(new NotificationScheduleResult
            {
                Accepted = true,
                RequestId = requestId,
                NotificationId = Guid.CreateVersion7(),
                QueuedCount = recipientCount,
                ScheduledForUtc = notBeforeUtc
            });
        }
    }
}
