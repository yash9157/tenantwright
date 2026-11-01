using Microsoft.EntityFrameworkCore;
using Shared.EventContracts;
using Shared.Uow.Abstractions;
using Tenant.Application.Tenants;
using Tenant.Domain.Shared.Enums;
using Tenant.Domain.Tenants;
using Tenant.EntityFrameworkCore.EntityFrameworkCore;
using Xunit;

namespace Tenant.Tests;

public partial class CurrentTenantProfileTests
{
    [Fact]
    public async Task Workflow_reconciliation_is_bounded_replays_terminal_changes_and_never_sends_invitations()
    {
        var currentTenant = new TestCurrentTenant(null);
        await using var db = CreateDbContext(currentTenant);
        var first = new TenantOwnershipRequest(Guid.CreateVersion7(), Guid.NewGuid(), null,
            "owner@example.test", null, "initial", null, TenantOwnershipRequestKind.InitialOwner);
        var second = new TenantOwnershipRequest(Guid.CreateVersion7(), Guid.NewGuid(), null,
            "other@example.test", null, "initial", null, TenantOwnershipRequestKind.InitialOwner);
        db.AddRange(first, second);
        await db.SaveChangesAsync();
        var sender = new RecordingMessageSender();
        var reconciler = new TenantOnboardingWorkflowReconciler(new WorkflowTestExecutor(),
            new StaticDbContextProvider<TenantDbContextApp>(db), sender, currentTenant, TimeProvider.System);

        Assert.Equal(1, await reconciler.CountPendingAsync(1, default));
        Assert.Equal(1, await reconciler.ReconcileAsync(1, default));
        Assert.Equal(1, await reconciler.ReconcileAsync(1, default));
        Assert.Equal(0, await reconciler.ReconcileAsync(1, default));
        Assert.Equal(2, sender.Commands.OfType<TenantInitialOwnerOnboardingRequestedEto>().Count());
        Assert.Empty(sender.Published);

        first.Cancel();
        await db.SaveChangesAsync();
        Assert.Equal(1, await reconciler.ReconcileAsync(1, default));
        var terminal = Assert.Single(sender.Commands.OfType<TenantInitialOwnerOnboardingCompletedEto>());
        Assert.Equal(first.Id, terminal.RequestId);
        Assert.Equal(first.TenantId, terminal.TenantId);
        Assert.Equal(TenantOwnershipRequestStatus.Canceled, first.WorkflowReconciledStatus);
        Assert.Equal(0, await reconciler.ReconcileAsync(1, default));
        Assert.Empty(sender.Published);
    }

    [Fact]
    public async Task Overdue_request_is_expired_by_its_owner_even_after_start_was_reconciled()
    {
        var currentTenant = new TestCurrentTenant(null);
        await using var db = CreateDbContext(currentTenant);
        var request = new TenantOwnershipRequest(Guid.CreateVersion7(), Guid.CreateVersion7(), null,
            "owner@example.test", null, "initial", null, TenantOwnershipRequestKind.InitialOwner,
            expiresAtUtc: DateTime.UnixEpoch.AddDays(7));
        request.RecordWorkflowReconciliation(request.Status);
        db.Add(request);
        await db.SaveChangesAsync();
        var sender = new RecordingMessageSender();
        var reconciler = new TenantOnboardingWorkflowReconciler(new WorkflowTestExecutor(),
            new StaticDbContextProvider<TenantDbContextApp>(db), sender, currentTenant, TimeProvider.System);
        Assert.Equal(1, await reconciler.ReconcileAsync(1, default));
        Assert.Equal(TenantOwnershipRequestStatus.Expired, request.Status);
        Assert.Single(sender.Published.OfType<TenantOwnershipInvitationCanceledEto>());
        Assert.Single(sender.Commands.OfType<TenantInitialOwnerOnboardingCompletedEto>());
        Assert.Equal(0, await reconciler.ReconcileAsync(1, default));
    }

    [Fact]
    public async Task Workflow_reconciliation_rejects_tenant_context_invalid_bounds_and_cancellation()
    {
        var currentTenant = new TestCurrentTenant(null);
        await using var db = CreateDbContext(currentTenant);
        var sender = new RecordingMessageSender();
        var reconciler = new TenantOnboardingWorkflowReconciler(new WorkflowTestExecutor(),
            new StaticDbContextProvider<TenantDbContextApp>(db), sender, currentTenant, TimeProvider.System);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reconciler.ReconcileAsync(101, default));
        using (currentTenant.Change(Guid.NewGuid()))
            await Assert.ThrowsAsync<InvalidOperationException>(() => reconciler.ReconcileAsync(1, default));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reconciler.ReconcileAsync(1, canceled.Token));
        Assert.Empty(sender.Commands);
    }

    // This fixture proves application branching, not relational transaction/outbox behavior.
    private sealed class WorkflowTestExecutor : IUnitOfWorkExecutor
    {
        public Task ExecuteTransactionalAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return operation(cancellationToken);
        }
        public Task<T> ExecuteTransactionalAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return operation(cancellationToken);
        }
        public Task ExecuteNonTransactionalAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<T> ExecuteNonTransactionalAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return operation(cancellationToken);
        }
    }
}
