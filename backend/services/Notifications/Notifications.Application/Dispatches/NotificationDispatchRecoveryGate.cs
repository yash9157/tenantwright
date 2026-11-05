using Microsoft.EntityFrameworkCore;
using Notifications.Domain.Notifications;
using Notifications.Domain.Shared.Enums;
using Notifications.EntityFrameworkCore.EntityFrameworkCore;
using Shared.Domain.Repositories;
using Shared.EntityFrameworkCore.Repositories;

namespace Notifications.Application.Dispatches;

internal static class NotificationDispatchRecoveryGate
{
    public const string DigestSourceService = "notifications-digest";

    public static async Task RejectDetachedCloneAsync(IRepository<Notification, Guid> repository,
        Notification notification, CancellationToken cancellationToken)
    {
        if (notification.DispatchStepId.HasValue || await IsRoutedDigestAsync(repository, notification, cancellationToken))
            throw NotificationDispatchGate.Conflict();
    }

    public static async Task<bool> IsRoutedDigestAsync(IRepository<Notification, Guid> repository, Notification notification,
        CancellationToken cancellationToken = default) =>
        notification.SourceService == DigestSourceService &&
        (await NotificationDispatchGate.ForNotificationAsync(await GetContextAsync(repository), notification, cancellationToken)).Count > 0;

    public static async Task ApplyAsync(IRepository<Notification, Guid> repository, Notification notification, bool retry,
        DateTime nowUtc, string actorId, CancellationToken cancellationToken)
    {
        if (!notification.DispatchStepId.HasValue)
        {
            if (notification.SourceService != DigestSourceService)
                return;
            var digestContext = await GetContextAsync(repository);
            var owners = await NotificationDispatchGate.ForNotificationAsync(digestContext, notification, cancellationToken);
            if (retry && owners.Count > 0)
                throw NotificationDispatchGate.Conflict();
            NotificationDispatchGate.RecordEvidence(owners, nowUtc);
            return;
        }
        var context = await GetContextAsync(repository);
        var scopeId = notification.TenantId ?? Guid.Empty;
        var step = await context.NotificationDispatchSteps.IgnoreQueryFilters().SingleAsync(candidate =>
            candidate.ScopeId == scopeId && candidate.Id == notification.DispatchStepId.Value, cancellationToken);
        var dispatch = await NotificationDispatchGate.LoadAsync(context, scopeId, step.DispatchId, cancellationToken);
        var route = dispatch.Recipients.Single(recipient => recipient.Id == step.RecipientId);
        var otherStageActive = dispatch.Mode == NotificationDispatchMode.OrderedFallback && route.Steps.Any(other =>
            other.Id != step.Id && (other.Ordinal < step.Ordinal && other.State is not (NotificationDispatchState.Rejected or NotificationDispatchState.Suppressed) ||
                other.Ordinal > step.Ordinal && other.State is not (NotificationDispatchState.Pending or NotificationDispatchState.Suppressed or NotificationDispatchState.Cancelled or NotificationDispatchState.Expired)));
        if (retry && (!dispatch.CanStartWork(nowUtc) || otherStageActive || step.OutcomeReason == "contradictory_delivery_evidence"))
            throw NotificationDispatchGate.Conflict();
        step.Observe(retry ? NotificationDispatchState.Queued : NotificationDispatchState.Accepted, "operator_recovery");
        if (!retry && otherStageActive)
        {
            step.Observe(NotificationDispatchState.NeedsReview, "contradictory_delivery_evidence");
            foreach (var other in route.Steps.Where(other => other.Ordinal > step.Ordinal))
                other.Observe(NotificationDispatchState.NeedsReview, "contradictory_delivery_evidence");
        }
        step.ScheduleEvaluation(nowUtc);
        dispatch.Record(NotificationDispatchEventKind.RecoveryRequested, nowUtc, route.Id, step.Id,
            retry ? "operator_retry" : "operator_acceptance_confirmed", actorId);
        dispatch.ScheduleEvaluation(nowUtc);
        dispatch.RefreshSummary();
    }

    private static async Task<NotificationsDbContextApp> GetContextAsync(IRepository<Notification, Guid> repository)
    {
        if (repository is IEfCoreRepository<Notification> efRepository &&
            await efRepository.GetDbContextAsync() is NotificationsDbContextApp context)
            return context;
        throw NotificationDispatchGate.Conflict();
    }
}
