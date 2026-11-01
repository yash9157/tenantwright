using Microsoft.EntityFrameworkCore;
using Shared.EntityFrameworkCore;
using Shared.EventBus.Messaging;
using Shared.EventContracts;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;
using Shared.Uow.Abstractions;
using Tenant.Domain.Shared.Enums;
using Tenant.EntityFrameworkCore.EntityFrameworkCore;

namespace Tenant.Application.Tenants;

public sealed class TenantOnboardingWorkflowReconciler(
    IUnitOfWorkExecutor unitOfWorkExecutor,
    IDbContextProvider<TenantDbContextApp> dbContextProvider,
    ITenantWrightMessageSender messages,
    ICurrentTenant currentTenant,
    TimeProvider timeProvider) : ITransientDependency
{
    public Task<int> CountPendingAsync(int ceiling, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ceiling, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ceiling, 1001);
        if (currentTenant.Id is not null)
            throw new InvalidOperationException("Onboarding workflow diagnostics require host context.");
        return unitOfWorkExecutor.ExecuteNonTransactionalAsync(async token =>
        {
            var db = await dbContextProvider.GetDbContextAsync();
            return await PendingRequests(db, timeProvider.GetUtcNow().UtcDateTime).Take(ceiling).CountAsync(token);
        }, cancellationToken);
    }

    public Task<int> ReconcileAsync(int batchSize, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(batchSize, 100);
        if (currentTenant.Id is not null)
            throw new InvalidOperationException("Onboarding workflow reconciliation requires host context.");
        return unitOfWorkExecutor.ExecuteTransactionalAsync(async token =>
        {
            var db = await dbContextProvider.GetDbContextAsync();
            var now = timeProvider.GetUtcNow().UtcDateTime;
            var requests = await PendingRequests(db, now)
                .OrderBy(request => request.CreationTime).ThenBy(request => request.Id)
                .Take(batchSize).ToArrayAsync(token);
            foreach (var request in requests)
            {
                var previousStatus = request.Status;
                request.ExpireIfDue(now);
                if (previousStatus != request.Status && request.Status == TenantOwnershipRequestStatus.Expired)
                    await messages.PublishEventAsync(new TenantOwnershipInvitationCanceledEto
                    {
                        RequestId = request.Id,
                        TenantId = request.TenantId,
                        OccurredAtUtc = now
                    }, token);
                await messages.SendCommandAsync(new TenantInitialOwnerOnboardingRequestedEto
                {
                    RequestId = request.Id,
                    TenantId = request.TenantId,
                    ExpiresAtUtc = request.ExpiresAtUtc,
                    OccurredAtUtc = request.CreationTime
                }, token);
                if (request.Status is TenantOwnershipRequestStatus.Completed or TenantOwnershipRequestStatus.Canceled or
                    TenantOwnershipRequestStatus.Expired or TenantOwnershipRequestStatus.Failed)
                    await messages.SendCommandAsync(new TenantInitialOwnerOnboardingCompletedEto
                    {
                        RequestId = request.Id,
                        TenantId = request.TenantId,
                        OccurredAtUtc = now
                    }, token);
                request.RecordWorkflowReconciliation(request.Status);
            }
            await db.SaveChangesAsync(token);
            return requests.Length;
        }, cancellationToken);
    }

    private static IQueryable<Domain.Tenants.TenantOwnershipRequest> PendingRequests(TenantDbContextApp db, DateTime now) =>
        db.TenantOwnershipRequests.Where(request => request.Kind == TenantOwnershipRequestKind.InitialOwner &&
            (request.WorkflowReconciledStatus == null || request.WorkflowReconciledStatus != request.Status ||
             ((request.Status == TenantOwnershipRequestStatus.PendingInvitation ||
               request.Status == TenantOwnershipRequestStatus.AwaitingEmailVerification) && request.ExpiresAtUtc <= now)));
}
