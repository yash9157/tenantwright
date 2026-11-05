using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Notifications.Application.Contracts.Delivery;
using Notifications.Application.Delivery;
using Notifications.Domain.Notifications;
using Notifications.Domain.Shared.Enums;
using Permissions.Application.Contracts.Features;
using Shared.BackgroundTasks.Workers;
using Shared.EventContracts;
using Xunit;

namespace Notifications.Tests;

/// <summary>
/// Regression coverage for the tenant-filter idempotency defect in
/// <see cref="NotificationDeliveryScheduler"/>. The scheduler runs from a distributed-event
/// handler with no ambient tenant context, so the global multi-tenant query filter collapses to
/// <c>TenantId == null</c>. Without <c>IgnoreQueryFilters()</c> the duplicate check can never match
/// its own <c>TenantId == input.TenantId</c> predicate, so a redelivered request is not recognized
/// as a duplicate.
/// </summary>
public class NotificationDeliverySchedulerIdempotencyTests
{
    [Fact]
    public async Task ScheduleAsync_dedupes_same_request_when_running_without_tenant_context()
    {
        var tenantId = Guid.NewGuid();
        var requestId = Guid.CreateVersion7();
        var databaseName = nameof(ScheduleAsync_dedupes_same_request_when_running_without_tenant_context);
        var databaseRoot = new InMemoryDatabaseRoot();

        // Each call uses a fresh DbContext over the same shared in-memory store, mirroring the
        // per-request scoping of two at-least-once deliveries of the same event.
        var first = await ScheduleSameRequestAsync(databaseName, databaseRoot, requestId, tenantId);
        var second = await ScheduleSameRequestAsync(databaseName, databaseRoot, requestId, tenantId);

        Assert.True(first.Accepted);
        Assert.False(first.IsDuplicate);
        Assert.Equal(1, first.QueuedCount);

        Assert.True(second.Accepted);
        Assert.True(second.IsDuplicate);
        Assert.Equal(0, second.QueuedCount);
        Assert.Equal(first.NotificationId, second.NotificationId);

        await using var countContext = NotificationsEfCoreTestHarness.BuildDbContext(
            databaseName, tenantId: null, multiTenantFilterEnabled: true, databaseRoot);
        var notificationCount = await countContext.Notifications.IgnoreQueryFilters().CountAsync();
        Assert.Equal(1, notificationCount);
    }

    [Fact]
    public async Task Harness_multi_tenant_filter_hides_tenant_rows_when_current_tenant_is_null()
    {
        // Guards against the idempotency test passing for the wrong reason: with the multi-tenant
        // filter enabled and no current tenant, a tenant-owned row must be invisible to a filtered
        // query and visible only through IgnoreQueryFilters().
        var tenantId = Guid.NewGuid();
        var databaseName = nameof(Harness_multi_tenant_filter_hides_tenant_rows_when_current_tenant_is_null);

        await using var dbContext = NotificationsEfCoreTestHarness.BuildDbContext(
            databaseName, tenantId: null, multiTenantFilterEnabled: true);

        dbContext.Notifications.Add(new Notification(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            requestId: Guid.NewGuid(),
            channel: NotificationChannel.Email,
            templateName: "Identity.SecurityRiskBlockedAlert",
            templateModelJson: "{}",
            subject: null));
        await dbContext.SaveChangesAsync();

        var filteredCount = await dbContext.Notifications.CountAsync();
        var unfilteredCount = await dbContext.Notifications.IgnoreQueryFilters().CountAsync();

        Assert.Equal(0, filteredCount);
        Assert.Equal(1, unfilteredCount);
    }

    private static async Task<NotificationScheduleResult> ScheduleSameRequestAsync(
        string databaseName,
        InMemoryDatabaseRoot databaseRoot,
        Guid requestId,
        Guid tenantId)
    {
        await using var dbContext = NotificationsEfCoreTestHarness.BuildDbContext(
            databaseName, tenantId: null, multiTenantFilterEnabled: true, databaseRoot);

        var scheduler = new NotificationDeliveryScheduler(
            new NoopUnitOfWorkManager(),
            new ImmediateUnitOfWorkExecutor(),
            new EfTestRepository<Notification, Guid>(dbContext),
            new EfTestRepository<NotificationRecipient, long>(dbContext),
            new EfTestRepository<NotificationDelivery, long>(dbContext),
            Options.Create(new NotificationsChannelsOptions { EmailEnabled = true }),
            Options.Create(new NotificationsDeliveryOptions()),
            new AlwaysEnabledFeatureCheckerClient(),
            new EnabledPreferenceClient(),
            new NoopWakeSignal(),
            new PassThroughRateLimiter(),
            new PassThroughAudienceResolver(),
            new NeverSuppressedTenantNotificationChecker(),
            new NoopNotificationDigestPlanner(),
            NullLogger<NotificationDeliveryScheduler>.Instance);

        return await scheduler.ScheduleAsync(new NotificationChannelRequestTenant
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
                    RecipientValue = "user@test.local"
                }
            ]
        });
    }

    private sealed class EnabledPreferenceClient : IIdentityNotificationPreferenceClient
    {
        public Task<bool> IsNotificationEnabledAsync(Guid? tenantId, Guid userId, string preferenceKey, CancellationToken cancellationToken = default)
            => Task.FromResult(true);
    }

    private sealed class AlwaysEnabledFeatureCheckerClient : IFeatureCheckerClient
    {
        public Task<bool> IsEnabledAsync(Guid? tenantId, string featureName, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<Dictionary<string, bool>> AreEnabledAsync(Guid? tenantId, IReadOnlyCollection<string> featureNames, CancellationToken cancellationToken = default)
            => Task.FromResult(featureNames.ToDictionary(x => x, _ => true, StringComparer.Ordinal));
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
