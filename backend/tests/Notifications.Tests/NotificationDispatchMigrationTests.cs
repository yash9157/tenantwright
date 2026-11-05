using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Notifications.Domain.Notifications;
using Xunit;

namespace Notifications.Tests;

[Collection(NotificationPostgresSuite.Name)]
[Trait("Category", "PostgresIntegration")]
public sealed class NotificationDispatchMigrationTests(NotificationPostgresFixture infrastructure)
{
    [Fact]
    public async Task Additive_migration_preserves_and_backfills_existing_destinations()
    {
        var connection = await infrastructure.CreateDatabaseAsync();
        var tenantId = Guid.NewGuid();
        await using var context = NotificationsEfCoreTestHarness.BuildPostgresDbContext(connection, tenantId);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20261017095048_NeutralMessagingOutbox");
        var notificationId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await context.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO "Notifications" ("Id", "TenantId", "RequestId", "Channel", "TemplateName", "TemplateModelJson", "SourceService", "Priority", "ExtraProperties", "ConcurrencyStamp", "CreationTime")
            VALUES ({{notificationId}}, {{tenantId}}, {{requestId}}, 1, 'test', '{}', 'notifications-tests', 2, '{}', 'test', {{now}});
            """);
        foreach (var address in new[] { "first@example.test", "second@example.test" })
        {
            await context.Database.ExecuteSqlInterpolatedAsync($$"""
                INSERT INTO "NotificationRecipients" ("TenantId", "NotificationId", "RecipientType", "RecipientValue")
                VALUES ({{tenantId}}, {{notificationId}}, 1, {{address}});
                INSERT INTO "NotificationDigestBuckets" ("Id", "TenantId", "ScopeKey", "Channel", "RecipientType", "RecipientValue", "Frequency", "PeriodStartUtc", "PeriodEndUtc", "Status", "AttemptCount", "ExtraProperties", "ConcurrencyStamp", "CreationTime")
                VALUES ({{Guid.NewGuid()}}, {{tenantId}}, {{tenantId.ToString("D")}}, 1, 1, {{address}}, 1, {{now.Date}}, {{now.Date.AddDays(1)}}, 1, 0, '{}', 'test', {{now}});
                """);
        }
        await migrator.MigrateAsync();
        Assert.False(context.Database.HasPendingModelChanges());
        var recipients = await context.NotificationRecipients.ToListAsync();
        Assert.Equal(2, recipients.Count);
        Assert.All(recipients, recipient => Assert.Equal(NotificationDestinationFingerprint.Compute(recipient.RecipientValue), recipient.RecipientValueHash));
        var buckets = await context.NotificationDigestBuckets.ToListAsync();
        Assert.Equal(2, buckets.Count);
        Assert.All(buckets, bucket => Assert.Equal(NotificationDestinationFingerprint.Compute(bucket.RecipientValue), bucket.RecipientValueHash));
        Assert.Equal(4, (await context.Database.GetAppliedMigrationsAsync()).Count());
    }
}
