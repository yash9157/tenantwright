using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Notifications.Application.Contracts.Delivery;
using Notifications.Application.Digests;
using Notifications.Domain.Archival;
using Notifications.Domain.Digests;
using Notifications.Domain.Notifications;
using Notifications.Domain.Policy;
using Notifications.Domain.Shared.Enums;
using Shared.EventContracts;
using Xunit;

namespace Notifications.Tests;

public class NotificationDigestTests
{
    [Fact]
    public async Task Tenant_preference_cannot_enable_digest_when_tenant_floor_is_disabled()
    {
        var tenantId = Guid.NewGuid();
        await using var dbContext = NotificationsEfCoreTestHarness.BuildDbContext(
            nameof(Tenant_preference_cannot_enable_digest_when_tenant_floor_is_disabled),
            tenantId);
        var preference = new UserNotificationPreference(Guid.NewGuid(), tenantId, "user-1");
        preference.Update(
            false,
            "UTC",
            0,
            0,
            NotificationPolicyChannel.None,
            NotificationDigestFrequency.Daily);
        dbContext.UserNotificationPreferences.Add(preference);
        await dbContext.SaveChangesAsync();

        var (notification, recipient) = NewSource(tenantId, "user-1");
        var planner = new NotificationDigestPlanner(NotificationsEfCoreTestHarness.AsDbContextProvider(dbContext));

        var diverted = await planner.TryAddAsync(notification, recipient, CancellationToken.None);

        Assert.False(diverted);
        Assert.Empty(dbContext.NotificationDigestBuckets);
        Assert.Empty(dbContext.NotificationDigestItems);
    }

    [Fact]
    public async Task Planner_retry_reuses_bucket_and_adds_source_once()
    {
        var tenantId = Guid.NewGuid();
        await using var dbContext = NotificationsEfCoreTestHarness.BuildDbContext(
            nameof(Planner_retry_reuses_bucket_and_adds_source_once),
            tenantId);
        var policy = new TenantNotificationPolicy(Guid.NewGuid(), tenantId);
        policy.Update(
            "UTC",
            false,
            0,
            0,
            NotificationPolicyChannel.None,
            [],
            NotificationDigestFrequency.Daily);
        dbContext.TenantNotificationPolicies.Add(policy);
        await dbContext.SaveChangesAsync();

        var (notification, recipient) = NewSource(tenantId, "user-1");
        var planner = new NotificationDigestPlanner(NotificationsEfCoreTestHarness.AsDbContextProvider(dbContext));

        Assert.True(await planner.TryAddAsync(notification, recipient, CancellationToken.None));
        await dbContext.SaveChangesAsync();
        Assert.True(await planner.TryAddAsync(notification, recipient, CancellationToken.None));
        await dbContext.SaveChangesAsync();

        Assert.Single(dbContext.NotificationDigestBuckets);
        Assert.Single(dbContext.NotificationDigestItems);
    }

    [Fact]
    public async Task Processor_emits_one_idempotent_neutral_delivery_keyed_by_bucket()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        await using var dbContext = NotificationsEfCoreTestHarness.BuildSqliteDbContext(
            connection,
            tenantId: null,
            multiTenantFilterEnabled: false);
        await dbContext.Database.EnsureCreatedAsync();
        var bucket = NewExpiredBucket();
        dbContext.NotificationDigestBuckets.Add(bucket);
        dbContext.NotificationDigestItems.Add(new NotificationDigestItem(
            1,
            tenantId: null,
            bucket.Id,
            Guid.NewGuid(),
            "Account.SensitiveTemplate",
            "Routine",
            DateTime.UtcNow.AddDays(-1)));
        await dbContext.SaveChangesAsync();

        var scheduler = new CapturingScheduler();
        var processor = new NotificationDigestProcessor(
            NotificationsEfCoreTestHarness.AsDbContextProvider(dbContext),
            new RelationalUnitOfWorkExecutor(dbContext),
            scheduler,
            new StaticOptionsMonitor<NotificationsDigestOptions>(new NotificationsDigestOptions()));

        Assert.Equal(1, await processor.ProcessBatchAsync(CancellationToken.None));
        Assert.Equal(0, await processor.ProcessBatchAsync(CancellationToken.None));

        var request = Assert.Single(scheduler.Requests);
        Assert.Equal(bucket.Id, request.RequestId);
        Assert.Equal("Notifications.Digest", request.TemplateName);
        Assert.Equal("Notifications.Digest", request.Category);
        Assert.Equal("Notification digest", request.Subject);
        Assert.Equal("1", request.TemplateModel["count"]);
        Assert.DoesNotContain("Account.SensitiveTemplate", request.TemplateModel.Values);
        Assert.Single(request.Recipients);
        Assert.Equal(NotificationDigestBucketStatus.Emitted, bucket.Status);
        Assert.Equal(scheduler.NotificationId, bucket.EmittedNotificationId);
    }

    [Fact]
    public async Task Empty_bucket_closes_without_fabricating_emitted_notification_id()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        await using var dbContext = NotificationsEfCoreTestHarness.BuildSqliteDbContext(
            connection,
            tenantId: null,
            multiTenantFilterEnabled: false);
        await dbContext.Database.EnsureCreatedAsync();
        var bucket = NewExpiredBucket();
        dbContext.NotificationDigestBuckets.Add(bucket);
        await dbContext.SaveChangesAsync();
        var scheduler = new CapturingScheduler();
        var processor = new NotificationDigestProcessor(
            NotificationsEfCoreTestHarness.AsDbContextProvider(dbContext),
            new RelationalUnitOfWorkExecutor(dbContext),
            scheduler,
            new StaticOptionsMonitor<NotificationsDigestOptions>(new NotificationsDigestOptions()));

        Assert.Equal(1, await processor.ProcessBatchAsync(CancellationToken.None));

        Assert.Equal(NotificationDigestBucketStatus.ClosedEmpty, bucket.Status);
        Assert.Null(bucket.EmittedNotificationId);
        Assert.NotNull(bucket.EmittedAtUtc);
        Assert.Empty(scheduler.Requests);
    }

    [Fact]
    public void Relational_model_enforces_digest_archive_and_receipt_integrity()
    {
        using var dbContext = NotificationsEfCoreTestHarness.BuildDbContext(
            nameof(Relational_model_enforces_digest_archive_and_receipt_integrity),
            tenantId: null,
            multiTenantFilterEnabled: false);

        var bucketType = dbContext.Model.FindEntityType(typeof(NotificationDigestBucket))!;
        Assert.Contains(bucketType.GetIndexes(), index =>
            index.IsUnique &&
            index.Properties.Select(property => property.Name).SequenceEqual(
                ["ScopeKey", "Channel", "RecipientType", "RecipientValueHash", "Frequency", "PeriodStartUtc"]));

        var itemType = dbContext.Model.FindEntityType(typeof(NotificationDigestItem))!;
        Assert.Contains(itemType.GetIndexes(), index =>
            index.IsUnique &&
            index.Properties.Select(property => property.Name).SequenceEqual(["BucketId", "SourceNotificationId"]));

        var archiveType = dbContext.Model.FindEntityType(typeof(ArchivedNotification))!;
        Assert.Contains(archiveType.GetIndexes(), index =>
            index.IsUnique && index.Properties.Single().Name == "SourceNotificationId");

        var receiptType = dbContext.Model.FindEntityType(typeof(NotificationDeliveryReceipt))!;
        Assert.Contains(receiptType.GetForeignKeys(), foreignKey =>
            foreignKey.PrincipalEntityType.ClrType == typeof(NotificationDelivery) &&
            foreignKey.DeleteBehavior == DeleteBehavior.Cascade);
    }

    private static (Notification Notification, NotificationRecipient Recipient) NewSource(Guid tenantId, string userId)
    {
        var notification = new Notification(
            Guid.NewGuid(),
            tenantId,
            Guid.NewGuid(),
            NotificationChannel.Email,
            "Routine.Template",
            "{}",
            "Routine",
            category: "Routine",
            priority: NotificationPriority.Normal,
            preferenceUserId: userId);
        var recipient = new NotificationRecipient(
            1,
            tenantId,
            notification.Id,
            NotificationRecipientType.EmailAddress,
            "user@test.local");
        return (notification, recipient);
    }

    private static NotificationDigestBucket NewExpiredBucket()
    {
        return new NotificationDigestBucket(
            Guid.NewGuid(),
            tenantId: null,
            NotificationChannel.Email,
            NotificationRecipientType.EmailAddress,
            "operator@test.local",
            NotificationDigestFrequency.Daily,
            DateTime.UtcNow.AddDays(-2),
            DateTime.UtcNow.AddDays(-1));
    }

    private sealed class CapturingScheduler : INotificationDeliveryScheduler
    {
        public Guid NotificationId { get; } = Guid.NewGuid();
        public List<CapturedNotificationRequest> Requests { get; } = [];

        public Task<NotificationScheduleResult> ScheduleAsync(
            NotificationChannelRequestTenant input,
            CancellationToken cancellationToken = default)
            => CaptureAsync(CapturedNotificationRequest.From(input));

        public Task<NotificationScheduleResult> ScheduleAsync(
            NotificationChannelRequestHost input,
            CancellationToken cancellationToken = default)
            => CaptureAsync(CapturedNotificationRequest.From(input));

        private Task<NotificationScheduleResult> CaptureAsync(CapturedNotificationRequest input)
        {
            Requests.Add(input);
            return Task.FromResult(new NotificationScheduleResult
            {
                Accepted = true,
                RequestId = input.RequestId,
                NotificationId = NotificationId,
                QueuedCount = 1
            });
        }
    }

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StaticOptionsMonitor(T currentValue) => CurrentValue = currentValue;

        public T CurrentValue { get; }

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
