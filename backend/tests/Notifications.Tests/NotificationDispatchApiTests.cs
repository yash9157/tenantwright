using Microsoft.EntityFrameworkCore;
using Notifications.Application.Contracts.Dispatches;
using Notifications.Application.Dispatches;
using Notifications.Domain.Shared.Enums;
using Shared;
using Shared.Domain.Entities;
using Shared.Permissions.Definitions.Notifications;
using Xunit;

namespace Notifications.Tests;

public sealed class NotificationDispatchApiTests
{
    [Fact]
    public async Task Read_permission_neither_discloses_sensitive_fields_nor_grants_cancellation()
    {
        var tenantId = Guid.NewGuid();
        await using var context = NotificationsEfCoreTestHarness.BuildDbContext(Guid.NewGuid().ToString(), tenantId, true);
        var dispatch = NotificationDispatchDomainTests.Dispatch(NotificationDispatchMode.Parallel, tenantId);
        NotificationDispatchDomainTests.AddRecipient(dispatch, "private-person-key");
        context.NotificationDispatches.Add(dispatch);
        await context.SaveChangesAsync();
        var service = Service(context, tenantId, NotificationsPermissions.Query.Read);
        var recipients = await service.GetRecipientsAsync(dispatch.Id, new NotificationDispatchPageInput(), true);
        var recipient = Assert.Single(recipients.Items);
        Assert.True(recipient.SensitiveFieldsWithheld);
        Assert.Null(recipient.RecipientKey);
        Assert.All(recipient.Steps, step => Assert.Null(step.ContentJson));
        await Assert.ThrowsAsync<SharedForbiddenException>(() => service.CancelAsync(dispatch.Id, true));

        var sensitive = await Service(context, tenantId, NotificationsPermissions.Query.Read, NotificationsPermissions.Query.ReadSensitivePayload)
            .GetRecipientsAsync(dispatch.Id, new NotificationDispatchPageInput(), true);
        Assert.Equal("private-person-key", Assert.Single(sensitive.Items).RecipientKey);
        Assert.All(Assert.Single(sensitive.Items).Steps, step => Assert.Equal("{}", step.ContentJson));
    }

    [Fact]
    public async Task Current_tenant_queries_reject_foreign_scope_and_host_context()
    {
        var tenantId = Guid.NewGuid();
        await using var context = NotificationsEfCoreTestHarness.BuildDbContext(Guid.NewGuid().ToString(), tenantId, true);
        var foreign = NotificationDispatchDomainTests.Dispatch(NotificationDispatchMode.Parallel, Guid.NewGuid());
        using (context.CurrentTenant.Change(foreign.TenantId))
        {
            context.NotificationDispatches.Add(foreign);
            await context.SaveChangesAsync();
        }
        var service = Service(context, tenantId, NotificationsPermissions.Query.Read);
        await Assert.ThrowsAsync<EntityNotFoundException>(() => service.GetAsync(foreign.Id, true));
        await Assert.ThrowsAsync<SharedForbiddenException>(() => service.GetListAsync(new GetNotificationDispatchesInput { TenantId = foreign.TenantId }, true));
        await Assert.ThrowsAsync<SharedForbiddenException>(() => Service(context, null, NotificationsPermissions.Query.Read).GetAsync(foreign.Id, true));
    }

    [Fact]
    public async Task Cancellation_has_its_own_permission_and_is_idempotently_recorded_with_the_operator()
    {
        var tenantId = Guid.NewGuid();
        await using var context = NotificationsEfCoreTestHarness.BuildDbContext(Guid.NewGuid().ToString(), tenantId, true);
        var dispatch = NotificationDispatchDomainTests.Dispatch(NotificationDispatchMode.Parallel, tenantId);
        NotificationDispatchDomainTests.AddRecipient(dispatch, "person");
        context.NotificationDispatches.Add(dispatch);
        await context.SaveChangesAsync();
        var service = Service(context, tenantId, NotificationsPermissions.Dispatches.Cancel);
        var first = await service.CancelAsync(dispatch.Id, true);
        var repeated = await service.CancelAsync(dispatch.Id, true);
        Assert.NotNull(first.CancelRequestedAtUtc);
        Assert.Equal(first.Revision, repeated.Revision);
        var cancellation = Assert.Single(await context.NotificationDispatchEvents.Where(history => history.Kind == NotificationDispatchEventKind.CancelRequested).ToListAsync());
        Assert.Equal("operator", cancellation.ActorId);
        await Assert.ThrowsAsync<SharedForbiddenException>(() => service.GetAsync(dispatch.Id, true));
    }

    private static NotificationDispatchAppService Service(Notifications.EntityFrameworkCore.EntityFrameworkCore.NotificationsDbContextApp context,
        Guid? tenantId, params string[] permissions)
    {
        var tenant = new TestCurrentTenant(tenantId);
        return new NotificationDispatchAppService(new ImmediateUnitOfWorkExecutor(), NotificationsEfCoreTestHarness.AsDbContextProvider(context),
            tenant, new NotificationsGrantedPermissionChecker(permissions), new TestCurrentPrincipalAccessor("operator"), new SilentDispatchAuditPublisher(),
            SharedOnlyTenantDatabases.FanOut(tenant), SharedOnlyTenantDatabases.Locator(tenant), TimeProvider.System);
    }

    [Fact]
    public async Task Childless_review_retry_requires_exact_permission_and_current_dispatch_revision()
    {
        var tenantId = Guid.NewGuid();
        await using var context = NotificationsEfCoreTestHarness.BuildDbContext(Guid.NewGuid().ToString(), tenantId, true);
        var dispatch = NotificationDispatchDomainTests.Dispatch(NotificationDispatchMode.Parallel, tenantId);
        var recipient = NotificationDispatchDomainTests.AddRecipient(dispatch, "person");
        var step = recipient.Steps.First();
        step.Observe(NotificationDispatchState.NeedsReview, "preparation_unavailable");
        dispatch.RefreshSummary();
        context.NotificationDispatches.Add(dispatch);
        await context.SaveChangesAsync();
        var input = new RetryNotificationDispatchStepInput { ExpectedRevision = dispatch.Revision };
        await Assert.ThrowsAsync<SharedForbiddenException>(() => Service(context, tenantId, NotificationsPermissions.Query.Read)
            .RetryStepAsync(dispatch.Id, step.Id, input, true));
        var service = Service(context, tenantId, NotificationsPermissions.Deliveries.Resend);
        await Assert.ThrowsAsync<SharedConflictException>(() => service.RetryStepAsync(dispatch.Id, step.Id,
            new RetryNotificationDispatchStepInput { ExpectedRevision = dispatch.Revision + 1 }, true));
        var result = await service.RetryStepAsync(dispatch.Id, step.Id, input, true);
        Assert.True(result.Revision > input.ExpectedRevision);
        Assert.Equal(NotificationDispatchState.Pending, step.State);
        Assert.Empty(await context.Notifications.ToListAsync());
    }
}
