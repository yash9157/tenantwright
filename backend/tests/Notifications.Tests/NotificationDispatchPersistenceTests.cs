using Microsoft.EntityFrameworkCore;
using Notifications.Domain.Dispatches;
using Notifications.Domain.Notifications;
using Notifications.Domain.Shared.Enums;
using Notifications.EntityFrameworkCore.EntityFrameworkCore;
using Shared.Data.Exceptions;
using Xunit;

namespace Notifications.Tests;

[Collection(NotificationPostgresSuite.Name)]
[Trait("Category", "PostgresIntegration")]
public sealed class NotificationDispatchPersistenceTests(NotificationPostgresFixture infrastructure)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Request_uniqueness_includes_nullable_host_scope(bool tenantScoped)
    {
        var connectionString = await DatabaseAsync();
        var tenantId = tenantScoped ? Guid.NewGuid() : (Guid?)null;
        var requestId = Guid.CreateVersion7();
        await using (var context = Context(connectionString, tenantId))
        {
            context.NotificationDispatches.Add(NotificationDispatchDomainTests.Dispatch(NotificationDispatchMode.Parallel, tenantId, requestId));
            await context.SaveChangesAsync();
        }
        await using var duplicate = Context(connectionString, tenantId);
        duplicate.NotificationDispatches.Add(NotificationDispatchDomainTests.Dispatch(NotificationDispatchMode.Parallel, tenantId, requestId));
        await AssertPostgresFailureAsync(() => duplicate.SaveChangesAsync(), "23505");
    }

    [Fact]
    public async Task Same_request_id_is_independent_across_tenant_and_host_scopes()
    {
        var connectionString = await DatabaseAsync();
        var requestId = Guid.CreateVersion7();
        var firstTenant = Guid.NewGuid();
        var secondTenant = Guid.NewGuid();
        await using (var context = Context(connectionString, null))
        {
            foreach (var scope in new Guid?[] { null, firstTenant, secondTenant })
                context.NotificationDispatches.Add(NotificationDispatchDomainTests.Dispatch(NotificationDispatchMode.Parallel, scope, requestId));
            await context.SaveChangesAsync();
        }
        await using var tenantContext = Context(connectionString, firstTenant, enableTenantFilter: true);
        Assert.Equal(firstTenant, Assert.Single(await tenantContext.NotificationDispatches.ToListAsync()).TenantId);
        await using var hostContext = Context(connectionString, null, enableTenantFilter: true);
        Assert.Null(Assert.Single(await hostContext.NotificationDispatches.ToListAsync()).TenantId);
    }

    [Fact]
    public async Task Database_rejects_a_recipient_linked_to_another_tenants_dispatch()
    {
        var connectionString = await DatabaseAsync();
        var dispatch = NotificationDispatchDomainTests.Dispatch(NotificationDispatchMode.Parallel, Guid.NewGuid());
        await using (var context = Context(connectionString, dispatch.TenantId))
        {
            context.NotificationDispatches.Add(dispatch);
            await context.SaveChangesAsync();
        }
        var foreignTenant = Guid.NewGuid();
        await using var foreignContext = Context(connectionString, foreignTenant);
        foreignContext.NotificationDispatchRecipients.Add(new NotificationDispatchRecipient(
            Guid.NewGuid(), foreignTenant, dispatch.Id, "foreign", null));
        await AssertPostgresFailureAsync(() => foreignContext.SaveChangesAsync(), "23503");
    }

    [Fact]
    public async Task Database_rejects_cross_scope_channel_notification_linkage()
    {
        var connectionString = await DatabaseAsync();
        var dispatch = NotificationDispatchDomainTests.Dispatch(NotificationDispatchMode.Parallel, Guid.NewGuid());
        var recipient = NotificationDispatchDomainTests.AddRecipient(dispatch, "recipient");
        await using (var context = Context(connectionString, dispatch.TenantId))
        {
            context.NotificationDispatches.Add(dispatch);
            await context.SaveChangesAsync();
        }
        var foreignTenant = Guid.NewGuid();
        var notification = new Notification(Guid.NewGuid(), foreignTenant, Guid.NewGuid(), NotificationChannel.Email, "test", "{}");
        notification.AttachDispatchStep(recipient.Steps.First().Id, foreignTenant);
        await using var foreignContext = Context(connectionString, foreignTenant);
        foreignContext.Notifications.Add(notification);
        await AssertPostgresFailureAsync(() => foreignContext.SaveChangesAsync(), "23503");
    }

    [Fact]
    public async Task Competing_dispatch_transitions_cannot_both_commit()
    {
        var connectionString = await DatabaseAsync();
        var tenantId = Guid.NewGuid();
        var dispatch = NotificationDispatchDomainTests.Dispatch(NotificationDispatchMode.OrderedFallback, tenantId);
        await using (var context = Context(connectionString, tenantId))
        {
            context.NotificationDispatches.Add(dispatch);
            await context.SaveChangesAsync();
        }
        await using var first = Context(connectionString, tenantId);
        await using var second = Context(connectionString, tenantId);
        var firstCopy = await first.NotificationDispatches.SingleAsync();
        var secondCopy = await second.NotificationDispatches.SingleAsync();
        firstCopy.Record(NotificationDispatchEventKind.StepActivated, NotificationDispatchDomainTests.Now);
        secondCopy.RequestCancellation(NotificationDispatchDomainTests.Now, "operator");
        await first.SaveChangesAsync();
        var failure = await Record.ExceptionAsync(() => second.SaveChangesAsync());
        Assert.NotNull(failure);
        if (failure is not SharedDbConcurrencyException)
        {
            var cause = failure;
            while (cause is not null && cause is not Npgsql.PostgresException)
                cause = cause.InnerException;
            var databaseFailure = Assert.IsType<Npgsql.PostgresException>(cause);
            Assert.Equal("23505", databaseFailure.SqlState);
            Assert.Equal("IX_NotificationDispatchEvents_ScopeId_DispatchId_Revision", databaseFailure.ConstraintName);
        }
        await using var verification = Context(connectionString, tenantId);
        Assert.Null((await verification.NotificationDispatches.SingleAsync()).CancelRequestedAtUtc);
        Assert.Equal(2, await verification.NotificationDispatchEvents.CountAsync());
    }

    [Fact]
    public async Task Dispatch_step_link_is_unique_and_protects_live_channel_rows_from_deletion()
    {
        var connectionString = await DatabaseAsync();
        var dispatch = NotificationDispatchDomainTests.Dispatch(NotificationDispatchMode.Parallel);
        var recipient = NotificationDispatchDomainTests.AddRecipient(dispatch, "recipient");
        var step = recipient.Steps.First();
        await using (var context = Context(connectionString, null))
        {
            context.NotificationDispatches.Add(dispatch);
            var notification = new Notification(Guid.NewGuid(), null, step.Id, NotificationChannel.Email, "test", "{}");
            notification.AttachDispatchStep(step.Id, Guid.Empty);
            context.Notifications.Add(notification);
            await context.SaveChangesAsync();
        }
        await using (var duplicate = Context(connectionString, null))
        {
            var notification = new Notification(Guid.NewGuid(), null, Guid.NewGuid(), NotificationChannel.Email, "test", "{}");
            notification.AttachDispatchStep(step.Id, Guid.Empty);
            duplicate.Notifications.Add(notification);
            await AssertPostgresFailureAsync(() => duplicate.SaveChangesAsync(), "23505");
        }
        await using var deletion = Context(connectionString, null);
        await AssertPostgresFailureAsync(() => deletion.NotificationDispatches.ExecuteDeleteAsync(), "23503");
    }

    private static async Task AssertPostgresFailureAsync(Func<Task> operation, string expectedSqlState)
    {
        var failure = await Record.ExceptionAsync(operation);
        Assert.NotNull(failure);
        var cause = failure;
        while (cause is not null && cause is not Npgsql.PostgresException)
            cause = cause.InnerException;
        Assert.Equal(expectedSqlState, Assert.IsType<Npgsql.PostgresException>(cause).SqlState);
    }

    [Fact]
    public async Task Large_push_destinations_use_bounded_fingerprint_uniqueness()
    {
        var connectionString = await DatabaseAsync();
        var tenantId = Guid.NewGuid();
        var destination = string.Concat(Enumerable.Range(0, 120).Select(_ => Guid.NewGuid().ToString("N")));
        var notification = new Notification(Guid.NewGuid(), tenantId, Guid.NewGuid(), NotificationChannel.Push, "push", "{}");
        await using (var context = Context(connectionString, tenantId))
        {
            context.Notifications.Add(notification);
            context.NotificationRecipients.Add(new NotificationRecipient(0, tenantId, notification.Id, NotificationRecipientType.DeviceToken, destination));
            await context.SaveChangesAsync();
        }
        await using var duplicate = Context(connectionString, tenantId);
        duplicate.NotificationRecipients.Add(new NotificationRecipient(0, tenantId, notification.Id, NotificationRecipientType.DeviceToken, destination));
        await AssertPostgresFailureAsync(() => duplicate.SaveChangesAsync(), "23505");
    }

    private async Task<string> DatabaseAsync()
    {
        var connectionString = await infrastructure.CreateDatabaseAsync();
        await using var context = Context(connectionString, null);
        await context.Database.EnsureCreatedAsync();
        return connectionString;
    }

    private static NotificationsDbContextApp Context(string connectionString, Guid? tenantId, bool enableTenantFilter = false) =>
        NotificationsEfCoreTestHarness.BuildPostgresDbContext(connectionString, tenantId, enableTenantFilter);
}
