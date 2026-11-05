using Microsoft.EntityFrameworkCore;
using Notifications.Domain.Dispatches;
using Notifications.Domain.Notifications;
using Notifications.Domain.Shared.Constants;
using Notifications.Domain.Shared.Enums;
using Notifications.EntityFrameworkCore.EntityFrameworkCore;
using Shared;

namespace Notifications.Application.Dispatches;

/// <summary>These gates run inside the transaction that changes the child. No external work belongs here.</summary>
internal static class NotificationDispatchGate
{
    public static Task<NotificationDispatch> LoadAsync(NotificationsDbContextApp context, Guid scopeId,
        Guid dispatchId, CancellationToken cancellationToken) => context.NotificationDispatches
        .IgnoreQueryFilters().Include(dispatch => dispatch.Recipients).ThenInclude(recipient => recipient.Steps)
        .SingleAsync(dispatch => dispatch.ScopeId == scopeId && dispatch.Id == dispatchId && !dispatch.IsDeleted,
            cancellationToken);

    public static async Task LinkAsync(NotificationsDbContextApp context, Notification notification,
        Guid stepId, Guid claimId, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var scopeId = notification.TenantId ?? Guid.Empty;
        var linkage = await context.NotificationDispatchSteps.IgnoreQueryFilters()
            .Where(step => step.ScopeId == scopeId && step.Id == stepId)
            .Select(step => new { step.DispatchId }).SingleOrDefaultAsync(cancellationToken)
            ?? throw Conflict();
        var dispatch = await LoadAsync(context, scopeId, linkage.DispatchId, cancellationToken);
        var step = dispatch.Recipients.SelectMany(recipient => recipient.Steps).Single(step => step.Id == stepId);
        if (!dispatch.CanStartWork(nowUtc) || !step.OwnsSchedulingClaim(claimId, nowUtc) || notification.RequestId != stepId || notification.Channel != step.Channel)
            throw Conflict();
        notification.AttachDispatchStep(stepId, scopeId);
        step.MarkScheduled();
        step.ScheduleEvaluation(nowUtc);
        dispatch.Record(NotificationDispatchEventKind.StepScheduled, nowUtc, step.RecipientId, stepId);
        dispatch.ScheduleEvaluation(nowUtc);
        dispatch.RefreshSummary();
    }

    public static async Task<NotificationDispatchState?> ClaimSendAsync(NotificationsDbContextApp context, Notification notification,
        DateTime nowUtc, CancellationToken cancellationToken)
    {
        var dispatches = await ForNotificationAsync(context, notification, cancellationToken);
        foreach (var (dispatch, step) in dispatches)
        {
            if (!dispatch.CanStartWork(nowUtc) || step.State is not (NotificationDispatchState.Queued or NotificationDispatchState.Deferred))
            {
                dispatch.Record(NotificationDispatchEventKind.OutcomeChanged, nowUtc, step.RecipientId, step.Id, "send_claim_blocked");
                step.ScheduleEvaluation(nowUtc);
                dispatch.ScheduleEvaluation(nowUtc);
                if (!notification.DispatchStepId.HasValue)
                    return NotificationDispatchState.NeedsReview;
                return dispatch.CancelRequestedAtUtc.HasValue ? NotificationDispatchState.Cancelled :
                    nowUtc >= dispatch.ExpiresAtUtc ? NotificationDispatchState.Expired : NotificationDispatchState.NeedsReview;
            }
        }
        foreach (var (dispatch, step) in dispatches)
        {
            dispatch.Record(NotificationDispatchEventKind.OutcomeChanged, nowUtc, step.RecipientId, step.Id, "send_claimed");
            dispatch.ScheduleEvaluation(nowUtc);
        }
        return null;
    }

    public static async Task DirtyAsync(NotificationsDbContextApp context, Notification notification,
        DateTime nowUtc, CancellationToken cancellationToken, bool contradictoryReceipt = false)
    {
        var owners = await ForNotificationAsync(context, notification, cancellationToken);
        RecordEvidence(owners, nowUtc);
        if (!contradictoryReceipt)
            return;
        foreach (var (owner, step) in owners)
        {
            var dispatch = await LoadAsync(context, owner.ScopeId, owner.Id, cancellationToken);
            var route = dispatch.Recipients.Single(recipient => recipient.Id == step.RecipientId);
            step.Observe(NotificationDispatchState.NeedsReview, "contradictory_delivery_evidence");
            if (dispatch.Mode == NotificationDispatchMode.OrderedFallback)
            {
                foreach (var later in route.Steps.Where(later => later.Ordinal > step.Ordinal &&
                    later.State is NotificationDispatchState.Scheduling or NotificationDispatchState.Queued or NotificationDispatchState.Deferred))
                    later.Observe(NotificationDispatchState.NeedsReview, "contradictory_delivery_evidence");
            }
            dispatch.RefreshSummary();
        }
    }

    public static void RecordEvidence(IReadOnlyList<(NotificationDispatch Dispatch, NotificationDispatchStep Step)> owners, DateTime nowUtc)
    {
        foreach (var (dispatch, step) in owners)
        {
            dispatch.Record(NotificationDispatchEventKind.OutcomeChanged, nowUtc, step.RecipientId, step.Id, "delivery_evidence_changed");
            step.ScheduleEvaluation(nowUtc);
            dispatch.ScheduleEvaluation(nowUtc);
        }
    }

    public static async Task<IReadOnlyList<(NotificationDispatch Dispatch, NotificationDispatchStep Step)>> ForNotificationAsync(
        NotificationsDbContextApp context, Notification notification, CancellationToken cancellationToken)
    {
        var scopeId = notification.TenantId ?? Guid.Empty;
        var stepIds = notification.DispatchStepId.HasValue ? new List<Guid> { notification.DispatchStepId.Value } :
            await (from bucket in context.NotificationDigestBuckets.IgnoreQueryFilters()
                   join item in context.NotificationDigestItems.IgnoreQueryFilters() on bucket.Id equals item.BucketId
                   join source in context.Notifications.IgnoreQueryFilters() on item.SourceNotificationId equals source.Id
                   where bucket.TenantId == notification.TenantId && item.TenantId == notification.TenantId &&
                         source.TenantId == notification.TenantId && bucket.EmittedNotificationId == notification.Id &&
                         source.DispatchStepId != null
                   select source.DispatchStepId!.Value).Distinct().Take(1001).ToListAsync(cancellationToken);
        return await LoadOwnersAsync(context, scopeId, stepIds, cancellationToken);
    }

    public static async Task<bool> LinkDigestAsync(NotificationsDbContextApp context, Notification notification,
        Guid bucketId, IReadOnlyList<NotificationRecipient> recipients, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var bucket = await context.NotificationDigestBuckets.IgnoreQueryFilters().SingleOrDefaultAsync(
            bucket => bucket.Id == bucketId && bucket.TenantId == notification.TenantId && bucket.Status == NotificationDigestBucketStatus.Processing,
            cancellationToken) ?? throw Conflict();
        if (notification.RequestId != bucketId || notification.Channel != bucket.Channel || recipients.Count != 1 ||
            recipients[0].RecipientType != bucket.RecipientType || recipients[0].RecipientValueHash != bucket.RecipientValueHash)
            throw Conflict();
        var sources = await (from item in context.NotificationDigestItems.IgnoreQueryFilters()
                             join source in context.Notifications.IgnoreQueryFilters() on item.SourceNotificationId equals source.Id
                             where item.BucketId == bucketId && item.TenantId == notification.TenantId && source.TenantId == notification.TenantId
                             select source.DispatchStepId).Take(1001).ToListAsync(cancellationToken);
        if (sources.Count > 1000)
        {
            bucket.RequireReview();
            return false;
        }
        var owners = await LoadOwnersAsync(context, notification.TenantId ?? Guid.Empty,
            sources.Where(stepId => stepId.HasValue).Select(stepId => stepId!.Value).Distinct().ToList(), cancellationToken);
        if (owners.Any(owner => !owner.Dispatch.CanStartWork(nowUtc) || owner.Step.State is not
            (NotificationDispatchState.Queued or NotificationDispatchState.Deferred)))
        {
            // One shared digest cannot retract a single message after composition. Hold mixed stopped/active intent
            // for review rather than sending cancelled content or pretending unrelated sources were cancelled.
            bucket.RequireReview();
            return false;
        }
        foreach (var (dispatch, step) in owners)
        {
            dispatch.Record(NotificationDispatchEventKind.OutcomeChanged, nowUtc, step.RecipientId, step.Id, "digest_linked");
            dispatch.ScheduleEvaluation(nowUtc);
            step.ScheduleEvaluation(nowUtc);
        }
        bucket.MarkEmitted(notification.Id, nowUtc);
        return true;
    }

    private static async Task<IReadOnlyList<(NotificationDispatch Dispatch, NotificationDispatchStep Step)>> LoadOwnersAsync(
        NotificationsDbContextApp context, Guid scopeId, List<Guid> stepIds, CancellationToken cancellationToken)
    {
        if (stepIds.Count == 0)
            return [];
        if (stepIds.Count > 1000)
            throw Conflict();
        var steps = await context.NotificationDispatchSteps.IgnoreQueryFilters()
            .Where(step => step.ScopeId == scopeId && stepIds.Contains(step.Id))
            .ToListAsync(cancellationToken);
        var ownerIds = steps.Select(step => step.DispatchId).Distinct().ToArray();
        var owners = await context.NotificationDispatches.IgnoreQueryFilters()
            .Where(dispatch => dispatch.ScopeId == scopeId && ownerIds.Contains(dispatch.Id) && !dispatch.IsDeleted)
            .ToDictionaryAsync(dispatch => dispatch.Id, cancellationToken);
        var result = steps.Where(step => owners.ContainsKey(step.DispatchId)).Select(step => (owners[step.DispatchId], step)).ToList();
        if (result.Count != stepIds.Count)
            throw Conflict();
        return result;
    }

    public static SharedConflictException Conflict() => new(NotificationErrorCodes.DispatchTransitionConflict,
        "The dispatch changed, was stopped, or no longer permits this channel operation.");
}
