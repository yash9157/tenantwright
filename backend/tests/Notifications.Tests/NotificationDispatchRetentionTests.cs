using Microsoft.EntityFrameworkCore;
using Notifications.Application.Retention;
using Notifications.Domain.Shared.Enums;
using Xunit;

namespace Notifications.Tests;

[Collection(NotificationPostgresSuite.Name)]
[Trait("Category", "PostgresIntegration")]
public sealed class NotificationDispatchRetentionTests(NotificationPostgresFixture infrastructure)
{
    [Fact]
    public async Task Archive_drains_bounded_history_before_removing_a_redacted_dispatch_graph()
    {
        var connection = await infrastructure.CreateDatabaseAsync();
        var tenantId = Guid.NewGuid();
        await using var context = NotificationsEfCoreTestHarness.BuildPostgresDbContext(connection, tenantId);
        await context.Database.EnsureCreatedAsync();
        var now = DateTime.UtcNow;
        var dispatch = NotificationDispatchDomainTests.Dispatch(NotificationDispatchMode.Parallel, tenantId);
        var recipient = NotificationDispatchDomainTests.AddRecipient(dispatch, "private-person@example.test");
        foreach (var step in recipient.Steps)
            step.Observe(NotificationDispatchState.Suppressed);
        dispatch.Record(NotificationDispatchEventKind.OutcomeChanged, now, actorId: "private-operator");
        dispatch.RefreshSummary();
        dispatch.StopPolling();
        dispatch.CreationTime = now.AddDays(-400);
        context.NotificationDispatches.Add(dispatch);
        await context.SaveChangesAsync();
        var executor = new RelationalUnitOfWorkExecutor(context);
        await executor.ExecuteTransactionalAsync(token => NotificationDispatchRetention.ArchiveBatchAsync(context, now.AddDays(-365), 1, now, token));
        Assert.Equal(1, await context.NotificationDispatches.CountAsync());
        Assert.Equal(1, await context.ArchivedNotificationDispatches.CountAsync());
        await executor.ExecuteTransactionalAsync(token => NotificationDispatchRetention.ArchiveBatchAsync(context, now.AddDays(-365), 1, now, token));
        Assert.Equal(0, await context.NotificationDispatches.CountAsync());
        Assert.Equal(0, await context.NotificationDispatchSteps.CountAsync());
        var archive = await context.ArchivedNotificationDispatches.ToListAsync();
        Assert.Equal(3, archive.Count);
        Assert.Single(archive, entry => entry.SourceEventId == Guid.Empty);
        Assert.All(archive, entry =>
        {
            Assert.DoesNotContain("private-person", entry.PayloadJson, StringComparison.Ordinal);
            Assert.DoesNotContain("private-operator", entry.PayloadJson, StringComparison.Ordinal);
            Assert.Equal(64, entry.PayloadSha256.Length);
        });
    }
}
