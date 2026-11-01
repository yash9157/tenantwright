using Microsoft.Extensions.Options;
using Notifications.Application.Contracts.UserNotifications;
using Notifications.Application.Notifications;
using Notifications.Application.UserNotifications;
using Notifications.Domain.Notifications;
using Notifications.Domain.Shared.Enums;
using Shared.Permissions.Definitions.Notifications;
using Xunit;

namespace Notifications.Tests;

/// <summary>
/// Behavior coverage for the inbox actions and filters added in sub-flow 5. Uses a real
/// <see cref="Notifications.EntityFrameworkCore.EntityFrameworkCore.NotificationsDbContextApp"/>
/// (InMemory) with the multi-tenant and soft-delete filters enabled and the current tenant/user
/// set, so tenant scoping and soft-delete hiding behave as in production.
/// </summary>
public class UserNotificationInboxActionsTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private const string UserId = "user-1";

    [Fact]
    public async Task GetInbox_excludes_archived_by_default_and_projects_new_fields()
    {
        using var db = BuildContext();
        var active = NewNotification(category: "security", priority: NotificationPriority.High, actionUrl: "/documents/1");
        var archivedNotification = NewNotification();
        db.Notifications.AddRange(active, archivedNotification);
        db.UserNotifications.Add(NewInbox(active.Id, 1));
        db.UserNotifications.Add(NewInbox(archivedNotification.Id, 2, archive: true));
        await db.SaveChangesAsync();

        var service = BuildService(db);
        var page = await service.GetInboxAsync(new GetUserNotificationsInput { MaxResultCount = 100 });

        var item = Assert.Single(page.Items);
        Assert.Equal(active.Id, item.NotificationId);
        Assert.Equal("security", item.Category);
        Assert.Equal(NotificationPriority.High, item.Priority);
        Assert.Equal("/documents/1", item.ActionUrl);
        Assert.False(item.IsArchived);
    }

    [Fact]
    public async Task GetInbox_only_archived_returns_archived_items()
    {
        using var db = BuildContext();
        var active = NewNotification();
        var archived = NewNotification();
        db.Notifications.AddRange(active, archived);
        db.UserNotifications.Add(NewInbox(active.Id, 1));
        db.UserNotifications.Add(NewInbox(archived.Id, 2, archive: true));
        await db.SaveChangesAsync();

        var service = BuildService(db);
        var page = await service.GetInboxAsync(new GetUserNotificationsInput { OnlyArchived = true, MaxResultCount = 100 });

        var item = Assert.Single(page.Items);
        Assert.Equal(archived.Id, item.NotificationId);
        Assert.True(item.IsArchived);
    }

    [Fact]
    public async Task GetInbox_filters_by_category_and_priority()
    {
        using var db = BuildContext();
        var security = NewNotification(category: "security", priority: NotificationPriority.High);
        var account = NewNotification(category: "account", priority: NotificationPriority.Normal);
        db.Notifications.AddRange(security, account);
        db.UserNotifications.Add(NewInbox(security.Id, 1));
        db.UserNotifications.Add(NewInbox(account.Id, 2));
        await db.SaveChangesAsync();

        var service = BuildService(db);

        var byCategory = await service.GetInboxAsync(new GetUserNotificationsInput { Category = "security", MaxResultCount = 100 });
        Assert.Equal(security.Id, Assert.Single(byCategory.Items).NotificationId);

        var byPriority = await service.GetInboxAsync(new GetUserNotificationsInput { Priority = NotificationPriority.Normal, MaxResultCount = 100 });
        Assert.Equal(account.Id, Assert.Single(byPriority.Items).NotificationId);
    }

    [Fact]
    public async Task GetAsync_returns_only_the_current_principals_safe_detail()
    {
        using var db = BuildContext();
        var notification = NewNotification(
            category: "security",
            priority: NotificationPriority.High,
            actionUrl: "/account/security");
        db.Notifications.Add(notification);
        var inbox = NewInbox(notification.Id, 1);
        inbox.SetRenderedContent("Security alert", "Review your account", "A new sign-in was detected.");
        db.UserNotifications.Add(inbox);
        await db.SaveChangesAsync();

        var detail = await BuildService(db).GetAsync(notification.Id);

        Assert.Equal(notification.Id, detail.NotificationId);
        Assert.Equal("Security alert", detail.Title);
        Assert.Equal("A new sign-in was detected.", detail.Body);
        Assert.Equal(string.Empty, detail.TemplateModelJson);
    }

    [Fact]
    public async Task GetAsync_unknown_notification_fails_closed()
    {
        using var db = BuildContext();
        var exception = await Assert.ThrowsAsync<Shared.Domain.Entities.EntityNotFoundException>(() =>
            BuildService(db).GetAsync(Guid.NewGuid()));

        Assert.Equal("notification_user_notification_not_found", exception.Code);
    }

    [Fact]
    public async Task MarkAllAsRead_marks_unread_non_archived_and_returns_count()
    {
        using var db = BuildContext();
        var n1 = NewNotification();
        var n2 = NewNotification();
        var n3 = NewNotification();
        var n4 = NewNotification();
        db.Notifications.AddRange(n1, n2, n3, n4);
        db.UserNotifications.Add(NewInbox(n1.Id, 1)); // unread
        db.UserNotifications.Add(NewInbox(n2.Id, 2)); // unread
        db.UserNotifications.Add(NewInbox(n3.Id, 3, read: true)); // already read
        db.UserNotifications.Add(NewInbox(n4.Id, 4, archive: true)); // archived (read)
        await db.SaveChangesAsync();

        var service = BuildService(db);
        var result = await service.MarkAllAsReadAsync(new MarkAllUserNotificationsReadInput
        {
            IdempotencyKey = Guid.NewGuid().ToString("D")
        });

        Assert.Equal(2, result.UpdatedCount);
        Assert.False(result.Receipt.IsReplay);
        Assert.Equal(2, result.Receipt.AffectedCount);
        var unreadRemaining = await service.GetInboxAsync(new GetUserNotificationsInput { OnlyUnread = true, MaxResultCount = 100 });
        Assert.Empty(unreadRemaining.Items);
    }

    [Fact]
    public async Task Mark_read_is_revision_fenced_and_replays_the_durable_receipt()
    {
        using var db = BuildContext();
        var notification = NewNotification();
        db.Notifications.Add(notification);
        db.UserNotifications.Add(NewInbox(notification.Id, 1));
        await db.SaveChangesAsync();
        var service = BuildService(db);
        var input = new MarkUserNotificationReadInput
        {
            NotificationId = notification.Id,
            ExpectedRevision = 1,
            IdempotencyKey = Guid.NewGuid().ToString("D")
        };

        var applied = await service.MarkAsReadAsync(input);
        var replay = await service.MarkAsReadAsync(input);

        Assert.Equal(2, applied.Revision);
        Assert.False(applied.Receipt.IsReplay);
        Assert.True(replay.Receipt.IsReplay);
        Assert.Equal(applied.Receipt.ReceiptId, replay.Receipt.ReceiptId);
        Assert.Single(db.NotificationOperationReceipts);

        var conflict = await Assert.ThrowsAsync<Shared.SharedConflictException>(() =>
            service.MarkAsReadAsync(new MarkUserNotificationReadInput
            {
                NotificationId = notification.Id,
                ExpectedRevision = 2,
                IdempotencyKey = input.IdempotencyKey
            }));
        Assert.Equal("notification_idempotency_payload_conflict", conflict.Code);
    }

    [Fact]
    public async Task Inbox_mutation_rejects_a_stale_revision_without_writing_a_receipt()
    {
        using var db = BuildContext();
        var notification = NewNotification();
        db.Notifications.Add(notification);
        db.UserNotifications.Add(NewInbox(notification.Id, 1));
        await db.SaveChangesAsync();

        var conflict = await Assert.ThrowsAsync<Shared.SharedConflictException>(() =>
            BuildService(db).ArchiveAsync(new ArchiveUserNotificationInput
            {
                NotificationId = notification.Id,
                ExpectedRevision = 2,
                IdempotencyKey = Guid.NewGuid().ToString("D")
            }));

        Assert.Equal("notification_revision_conflict", conflict.Code);
        Assert.Empty(db.NotificationOperationReceipts);
    }

    [Fact]
    public async Task Archive_marks_read_and_hides_from_default_inbox()
    {
        using var db = BuildContext();
        var n = NewNotification();
        db.Notifications.Add(n);
        db.UserNotifications.Add(NewInbox(n.Id, 1));
        await db.SaveChangesAsync();

        var service = BuildService(db);
        await service.ArchiveAsync(new ArchiveUserNotificationInput
        {
            NotificationId = n.Id,
            ExpectedRevision = 1,
            IdempotencyKey = Guid.NewGuid().ToString("D")
        });

        var defaultInbox = await service.GetInboxAsync(new GetUserNotificationsInput { MaxResultCount = 100 });
        Assert.Empty(defaultInbox.Items);

        var archived = await service.GetInboxAsync(new GetUserNotificationsInput { OnlyArchived = true, MaxResultCount = 100 });
        var item = Assert.Single(archived.Items);
        Assert.True(item.IsArchived);
        Assert.True(item.IsRead);
    }

    [Fact]
    public async Task Delete_removes_from_inbox()
    {
        using var db = BuildContext();
        var n = NewNotification();
        db.Notifications.Add(n);
        db.UserNotifications.Add(NewInbox(n.Id, 1));
        await db.SaveChangesAsync();

        var service = BuildService(db);
        await service.DeleteAsync(new DeleteUserNotificationInput
        {
            NotificationId = n.Id,
            ExpectedRevision = 1,
            IdempotencyKey = Guid.NewGuid().ToString("D")
        });

        var inbox = await service.GetInboxAsync(new GetUserNotificationsInput { IncludeArchived = true, MaxResultCount = 100 });
        Assert.Empty(inbox.Items);
    }

    [Fact]
    public async Task Inbox_application_boundary_denies_missing_exact_permission()
    {
        using var db = BuildContext();
        var service = new UserNotificationAppService(
            new EfTestRepository<UserNotification, long>(db),
            NotificationsEfCoreTestHarness.AsDbContextProvider(db),
            new TestCurrentTenant(Tenant),
            new TestCurrentPrincipalAccessor(UserId),
            Options.Create(new NotificationsQuerySecurityOptions()),
            new NotificationsGrantedPermissionChecker(),
            TimeProvider.System);

        await Assert.ThrowsAsync<Shared.SharedForbiddenException>(() =>
            service.GetInboxAsync(new GetUserNotificationsInput()));
    }

    private static Notifications.EntityFrameworkCore.EntityFrameworkCore.NotificationsDbContextApp BuildContext()
    {
        return NotificationsEfCoreTestHarness.BuildDbContext(
            Guid.NewGuid().ToString(), tenantId: Tenant, multiTenantFilterEnabled: true);
    }

    private static UserNotificationAppService BuildService(
        Notifications.EntityFrameworkCore.EntityFrameworkCore.NotificationsDbContextApp db)
    {
        return new UserNotificationAppService(
            new EfTestRepository<UserNotification, long>(db),
            NotificationsEfCoreTestHarness.AsDbContextProvider(db),
            new TestCurrentTenant(Tenant),
            new TestCurrentPrincipalAccessor(UserId),
            Options.Create(new NotificationsQuerySecurityOptions()),
            new NotificationsGrantedPermissionChecker(
                NotificationsPermissions.Inbox.Read,
                NotificationsPermissions.Inbox.MarkRead,
                NotificationsPermissions.Inbox.Archive,
                NotificationsPermissions.Inbox.Delete),
            TimeProvider.System);
    }

    private static Notification NewNotification(
        string? category = null,
        NotificationPriority priority = NotificationPriority.Normal,
        string? actionUrl = null)
    {
        return new Notification(
            id: Guid.NewGuid(),
            tenantId: Tenant,
            requestId: Guid.NewGuid(),
            channel: NotificationChannel.App,
            templateName: "Tpl",
            templateModelJson: "{}",
            subject: "S",
            category: category,
            priority: priority,
            actionUrl: actionUrl);
    }

    private static UserNotification NewInbox(Guid notificationId, long id, bool read = false, bool archive = false)
    {
        var inbox = new UserNotification(id, Tenant, notificationId, UserId);
        if (archive)
        {
            inbox.Archive(DateTime.UtcNow);
        }
        else if (read)
        {
            inbox.MarkRead(DateTime.UtcNow);
        }

        return inbox;
    }
}
