using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Notifications.Application.Retention;
using Notifications.Domain.Archival;
using Notifications.Domain.Notifications;
using Notifications.Domain.Presence;
using Notifications.Domain.Shared.Enums;
using Xunit;

namespace Notifications.Tests;

/// <summary>
/// Relational coverage for archive-first notification retention. SQLite is used because the service
/// relies on set-based deletes and database cascades, neither of which the InMemory provider models.
/// Tenant filters remain enabled in host context so these tests also prove that the background path
/// performs only its explicit, service-owned filter bypasses.
/// </summary>
public class NotificationRetentionServiceTests
{
    private static readonly DateTime Now = DateTime.UtcNow;
    private static readonly Guid TenantId = Guid.NewGuid();

    [Fact]
    public async Task Archives_complete_aggregate_before_removing_hot_rows()
    {
        using var connection = OpenDatabase();
        var notification = NewNotification(Now.AddDays(-400));
        var recipient = NewRecipient(notification.Id, 1);
        var delivery = NewDelivery(notification.Id, recipient.Id, Now.AddDays(-400),
            item => item.MarkProviderAccepted(Now.AddDays(-400), "provider-message-1"));
        var receipt = new NotificationDeliveryReceipt(
            id: 1,
            tenantId: TenantId,
            deliveryId: delivery.Id,
            providerConfigId: null,
            providerName: "smtp-provider",
            providerEventId: "event-1",
            eventType: EmailDeliveryEventType.Delivered,
            providerCode: "250",
            safeReason: null,
            occurredAtUtc: Now.AddDays(-400),
            receivedAtUtc: Now.AddDays(-400));

        await SeedAsync(connection,
            notification,
            recipient,
            delivery,
            receipt,
            NewInbox(notification.Id, 1, Now.AddDays(-400)));

        var result = await RunPurgeAsync(connection);

        Assert.Equal(1, result.ArchivedNotifications);
        Assert.Equal(0, result.PurgedPresenceRecords);

        await using var verificationContext = CreateContext(connection);
        var archive = await verificationContext.ArchivedNotifications
            .IgnoreQueryFilters()
            .SingleAsync();
        Assert.Equal(notification.Id, archive.SourceNotificationId);
        Assert.Equal(notification.TenantId, archive.TenantId);
        Assert.Equal(notification.CreationTime, archive.SourceCreatedAtUtc);
        Assert.Matches("^[0-9a-f]{64}$", archive.PayloadSha256);
        Assert.Contains("\"providerEventId\":\"event-1\"", archive.PayloadJson, StringComparison.Ordinal);
        Assert.Contains("\"recipientValue\":\"recipient-1@test.local\"", archive.PayloadJson, StringComparison.Ordinal);

        var counts = await CountsAsync(connection);
        Assert.Equal(0, counts.Notifications);
        Assert.Equal(0, counts.Recipients);
        Assert.Equal(0, counts.Deliveries);
        Assert.Equal(0, counts.Receipts);
        Assert.Equal(0, counts.Inbox);
        Assert.Equal(1, counts.Archives);
    }

    [Fact]
    public async Task Enforces_twelve_month_hot_retention_even_when_configuration_is_lower()
    {
        using var connection = OpenDatabase();
        var notification = NewNotification(Now.AddDays(-200));
        var recipient = NewRecipient(notification.Id, 1);
        await SeedAsync(connection,
            notification,
            recipient,
            NewDelivery(notification.Id, recipient.Id, Now.AddDays(-200),
                item => item.MarkProviderAccepted(Now.AddDays(-200), "provider-message-1")));

        var result = await RunPurgeAsync(connection, options => options.HotRetentionDays = 30);

        Assert.Equal(0, result.Total);
        var counts = await CountsAsync(connection);
        Assert.Equal(1, counts.Notifications);
        Assert.Equal(0, counts.Archives);
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("processing")]
    [InlineData("failed")]
    [InlineData("needs-review")]
    public async Task Does_not_archive_notification_with_non_terminal_delivery(string state)
    {
        using var connection = OpenDatabase();
        var notification = NewNotification(Now.AddDays(-400));
        var recipient = NewRecipient(notification.Id, 1);
        await SeedAsync(connection,
            notification,
            recipient,
            NewDelivery(notification.Id, recipient.Id, Now.AddDays(-400), item => ApplyState(item, state)));

        var result = await RunPurgeAsync(connection);

        Assert.Equal(0, result.ArchivedNotifications);
        var counts = await CountsAsync(connection);
        Assert.Equal(1, counts.Notifications);
        Assert.Equal(0, counts.Archives);
    }

    [Fact]
    public async Task Recovers_idempotently_when_archive_was_saved_before_hot_delete()
    {
        using var connection = OpenDatabase();
        var notification = NewNotification(Now.AddDays(-400));
        var recipient = NewRecipient(notification.Id, 1);
        var existingArchive = new ArchivedNotification(
            Guid.NewGuid(),
            TenantId,
            notification.Id,
            notification.CreationTime,
            Now.AddMinutes(-5),
            "{}",
            new string('a', 64));
        await SeedAsync(connection,
            notification,
            recipient,
            NewDelivery(notification.Id, recipient.Id, Now.AddDays(-400),
                item => item.MarkDeadLettered("terminal", Now.AddDays(-400))),
            existingArchive);

        var first = await RunPurgeAsync(connection);
        var second = await RunPurgeAsync(connection);

        Assert.Equal(1, first.ArchivedNotifications);
        Assert.Equal(0, second.Total);
        var counts = await CountsAsync(connection);
        Assert.Equal(0, counts.Notifications);
        Assert.Equal(1, counts.Archives);
    }

    [Fact]
    public async Task Purges_presence_after_ninety_days_and_keeps_newer_observations()
    {
        using var connection = OpenDatabase();
        await SeedAsync(connection,
            new NotificationPresenceRecord(Guid.NewGuid(), TenantId, "expired-user", Now.AddDays(-100)),
            new NotificationPresenceRecord(Guid.NewGuid(), TenantId, "current-user", Now.AddDays(-89)));

        var result = await RunPurgeAsync(connection, options => options.PresenceRetentionDays = 1);

        Assert.Equal(1, result.PurgedPresenceRecords);
        await using var verificationContext = CreateContext(connection);
        var remaining = await verificationContext.NotificationPresenceRecords
            .IgnoreQueryFilters()
            .SingleAsync();
        Assert.Equal("current-user", remaining.UserId);
    }

    [Fact]
    public async Task Applies_batch_limit_to_archive_and_presence_work()
    {
        using var connection = OpenDatabase();
        var firstNotification = NewNotification(Now.AddDays(-410));
        var firstRecipient = NewRecipient(firstNotification.Id, 1);
        var secondNotification = NewNotification(Now.AddDays(-400));
        var secondRecipient = NewRecipient(secondNotification.Id, 2);
        await SeedAsync(connection,
            firstNotification,
            firstRecipient,
            NewDelivery(firstNotification.Id, firstRecipient.Id, Now.AddDays(-410),
                item => item.MarkProviderAccepted(Now.AddDays(-410), "provider-message-1")),
            secondNotification,
            secondRecipient,
            NewDelivery(secondNotification.Id, secondRecipient.Id, Now.AddDays(-400),
                item => item.MarkProviderAccepted(Now.AddDays(-400), "provider-message-2")),
            new NotificationPresenceRecord(Guid.NewGuid(), TenantId, "expired-user-1", Now.AddDays(-110)),
            new NotificationPresenceRecord(Guid.NewGuid(), TenantId, "expired-user-2", Now.AddDays(-100)));

        var result = await RunPurgeAsync(connection, options => options.BatchSize = 1);

        Assert.Equal(1, result.ArchivedNotifications);
        Assert.Equal(1, result.PurgedPresenceRecords);
        var counts = await CountsAsync(connection);
        Assert.Equal(1, counts.Notifications);
        Assert.Equal(1, counts.Archives);
        Assert.Equal(1, counts.Presence);
    }

    [Fact]
    public async Task Disabled_retention_does_not_modify_hot_archive_or_presence_rows()
    {
        using var connection = OpenDatabase();
        var notification = NewNotification(Now.AddDays(-500));
        var recipient = NewRecipient(notification.Id, 1);
        await SeedAsync(connection,
            notification,
            recipient,
            NewDelivery(notification.Id, recipient.Id, Now.AddDays(-500),
                item => item.MarkProviderAccepted(Now.AddDays(-500), "provider-message-1")),
            new NotificationPresenceRecord(Guid.NewGuid(), TenantId, "expired-user", Now.AddDays(-100)));

        var result = await RunPurgeAsync(connection, options => options.Enabled = false);

        Assert.Equal(0, result.Total);
        var counts = await CountsAsync(connection);
        Assert.Equal(1, counts.Notifications);
        Assert.Equal(0, counts.Archives);
        Assert.Equal(1, counts.Presence);
    }

    private static void ApplyState(NotificationDelivery delivery, string state)
    {
        switch (state)
        {
            case "queued":
                break;
            case "processing":
                delivery.MarkProcessing(Now.AddDays(-400), Now.AddDays(-400).AddMinutes(5));
                break;
            case "failed":
                delivery.MarkFailed("retryable", Now.AddDays(-400).AddMinutes(5));
                break;
            case "needs-review":
                delivery.MarkNeedsReview("ambiguous", Now.AddDays(-400));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(state), state, null);
        }
    }

    private static SqliteConnection OpenDatabase()
    {
        var connection = new SqliteConnection("DataSource=:memory:;Foreign Keys=True");
        connection.Open();
        using var context = CreateContext(connection);
        context.Database.EnsureCreated();
        return connection;
    }

    private static Notifications.EntityFrameworkCore.EntityFrameworkCore.NotificationsDbContextApp CreateContext(
        SqliteConnection connection)
    {
        return NotificationsEfCoreTestHarness.BuildSqliteDbContext(
            connection,
            tenantId: null,
            multiTenantFilterEnabled: true);
    }

    private static Notification NewNotification(DateTime creationTime)
    {
        return new Notification(
            Guid.NewGuid(),
            TenantId,
            Guid.NewGuid(),
            NotificationChannel.Email,
            "Notifications.Retention",
            "{\"message\":\"archive me\"}",
            "Retention test")
        {
            CreationTime = creationTime
        };
    }

    private static NotificationRecipient NewRecipient(Guid notificationId, long id)
    {
        return new NotificationRecipient(
            id,
            TenantId,
            notificationId,
            NotificationRecipientType.EmailAddress,
            $"recipient-{id}@test.local");
    }

    private static NotificationDelivery NewDelivery(
        Guid notificationId,
        long recipientId,
        DateTime creationTime,
        Action<NotificationDelivery> configure)
    {
        var delivery = new NotificationDelivery(recipientId, TenantId, notificationId, recipientId)
        {
            CreationTime = creationTime
        };
        configure(delivery);
        return delivery;
    }

    private static UserNotification NewInbox(Guid notificationId, long id, DateTime creationTime)
    {
        return new UserNotification(id, TenantId, notificationId, $"user-{id}")
        {
            CreationTime = creationTime
        };
    }

    private static async Task SeedAsync(SqliteConnection connection, params object[] entities)
    {
        await using var context = CreateContext(connection);
        context.AddRange(entities);
        await context.SaveChangesAsync();
    }

    private static async Task<NotificationRetentionResult> RunPurgeAsync(
        SqliteConnection connection,
        Action<NotificationsRetentionOptions>? configure = null)
    {
        var options = new NotificationsRetentionOptions
        {
            Enabled = true,
            HotRetentionDays = 365,
            PresenceRetentionDays = 90,
            BatchSize = 500,
            IntervalSeconds = 3600
        };
        configure?.Invoke(options);

        await using var context = CreateContext(connection);
        var service = new NotificationRetentionService(
            NotificationsEfCoreTestHarness.AsDbContextProvider(context),
            new ImmediateUnitOfWorkExecutor(),
            new StaticOptionsMonitor<NotificationsRetentionOptions>(options),
            NullLogger<NotificationRetentionService>.Instance);
        return await service.PurgeAsync();
    }

    private static async Task<RetentionCounts> CountsAsync(SqliteConnection connection)
    {
        await using var context = CreateContext(connection);
        return new RetentionCounts(
            await context.Notifications.IgnoreQueryFilters().CountAsync(),
            await context.NotificationRecipients.IgnoreQueryFilters().CountAsync(),
            await context.NotificationDeliveries.IgnoreQueryFilters().CountAsync(),
            await context.NotificationDeliveryReceipts.IgnoreQueryFilters().CountAsync(),
            await context.UserNotifications.IgnoreQueryFilters().CountAsync(),
            await context.ArchivedNotifications.IgnoreQueryFilters().CountAsync(),
            await context.NotificationPresenceRecords.IgnoreQueryFilters().CountAsync());
    }

    private sealed record RetentionCounts(
        int Notifications,
        int Recipients,
        int Deliveries,
        int Receipts,
        int Inbox,
        int Archives,
        int Presence);

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StaticOptionsMonitor(T value) => CurrentValue = value;

        public T CurrentValue { get; }

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
