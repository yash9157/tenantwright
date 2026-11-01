using Microsoft.EntityFrameworkCore;
using Notifications.Domain.Notifications;
using Notifications.Domain.Policy;
using Notifications.EntityFrameworkCore.EntityFrameworkCore;
using Xunit;

namespace Notifications.Tests;

public sealed class NotificationsPersistenceModelTests
{
    [Fact]
    public void PostgreSQL_model_enforces_operation_idempotency_and_revision_concurrency()
    {
        using var db = new NotificationsDbContextApp(
            new DbContextOptionsBuilder<NotificationsDbContextApp>()
                .UseNpgsql("Host=localhost;Database=notifications_model_only;Username=unused;Password=unused")
                .Options);

        var receipt = db.Model.FindEntityType(typeof(NotificationOperationReceipt))!;
        Assert.Contains(receipt.GetIndexes(), index =>
            index.IsUnique &&
            index.Properties.Select(property => property.Name).SequenceEqual([
                nameof(NotificationOperationReceipt.ScopeKey),
                nameof(NotificationOperationReceipt.ActorUserId),
                nameof(NotificationOperationReceipt.IdempotencyKey)
            ]));

        Assert.True(db.Model.FindEntityType(typeof(UserNotification))!
            .FindProperty(nameof(UserNotification.Revision))!.IsConcurrencyToken);
        Assert.True(db.Model.FindEntityType(typeof(TenantNotificationPolicy))!
            .FindProperty(nameof(TenantNotificationPolicy.Revision))!.IsConcurrencyToken);
        Assert.True(db.Model.FindEntityType(typeof(UserNotificationPreference))!
            .FindProperty(nameof(UserNotificationPreference.Revision))!.IsConcurrencyToken);
    }
}
