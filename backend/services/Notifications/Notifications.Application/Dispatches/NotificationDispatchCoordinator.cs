using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Notifications.Application.Contracts.Delivery;
using Notifications.Application.Contracts.Dispatches;
using Notifications.Application.Delivery;
using Notifications.Application.RateLimiting;
using Notifications.Domain.Dispatches;
using Notifications.Domain.Notifications;
using Notifications.Domain.Shared.Enums;
using Notifications.EntityFrameworkCore.EntityFrameworkCore;
using Shared;
using Shared.EntityFrameworkCore;
using Shared.EntityFrameworkCore.TenantDatabases;
using Shared.MultiTenancy;
using Shared.Uow.Abstractions;

namespace Notifications.Application.Dispatches;

/// <summary>One bounded database phase, preparation outside a transaction, then a fenced completion.</summary>
public sealed class NotificationDispatchCoordinator(
    IUnitOfWorkExecutor unitOfWorkExecutor,
    IDbContextProvider<NotificationsDbContextApp> contextProvider,
    INotificationDeliveryScheduler scheduler,
    ICurrentTenant currentTenant,
    TimeProvider timeProvider,
    IOptions<NotificationDispatchOptions> options,
    ILogger<NotificationDispatchCoordinator> logger) : INotificationDispatchCoordinator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly DateTime LastUtc = DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc);

    public async Task<int> ProcessBatchAsync(CancellationToken cancellationToken = default)
    {
        var batchStarted = Stopwatch.GetTimestamp();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var due = await unitOfWorkExecutor.ExecuteNonTransactionalAsync(async token =>
        {
            var context = await contextProvider.GetDbContextAsync();
            return await (from step in context.NotificationDispatchSteps.IgnoreQueryFilters().WhereOwnedByCurrentDatabase()
                          join dispatch in context.NotificationDispatches.IgnoreQueryFilters().WhereOwnedByCurrentDatabase()
                              on new { step.ScopeId, Id = step.DispatchId } equals new { dispatch.ScopeId, dispatch.Id }
                          where !dispatch.IsDeleted && dispatch.NextEvaluationAtUtc != null &&
                                dispatch.NextEvaluationAtUtc <= now && step.NextEvaluationAtUtc <= now
                          orderby step.NextEvaluationAtUtc, step.Id
                          select new DueStep(step.Id, dispatch.Id, step.TenantId, dispatch.Mode, step.Channel))
                .Take(options.Value.ReconciliationBatchSize).ToListAsync(token);
        }, cancellationToken);

        foreach (var candidate in due)
        {
            if (Stopwatch.GetElapsedTime(batchStarted) >= TimeSpan.FromSeconds(30))
                break;
            cancellationToken.ThrowIfCancellationRequested();
            using var tenantScope = currentTenant.Change(candidate.TenantId);
            try
            {
                var claim = await unitOfWorkExecutor.ExecuteTransactionalAsync(
                    token => ReconcileAsync(candidate, token), cancellationToken);
                NotificationDispatchTelemetry.Evaluation(candidate.Mode, candidate.Channel);
                if (claim is not null)
                    await PrepareAsync(claim, cancellationToken);
            }
            catch (SharedConflictException)
            {
                // Nothing external happens before the scheduling claim commits. A fresh poll observes the winner.
                logger.LogDebug("Notification dispatch transition raced for step {StepId}; durable polling will reconcile it.", candidate.StepId);
            }
            catch (Shared.Data.Exceptions.SharedDbConcurrencyException)
            {
                logger.LogDebug("Notification dispatch revision changed for step {StepId}; durable polling will reconcile it.", candidate.StepId);
            }
        }
        return due.Count;
    }

    private async Task<SchedulingClaim?> ReconcileAsync(DueStep candidate, CancellationToken cancellationToken)
    {
        var context = await contextProvider.GetDbContextAsync();
        var dispatch = await NotificationDispatchGate.LoadAsync(context, candidate.TenantId ?? Guid.Empty,
            candidate.DispatchId, cancellationToken);
        var recipient = dispatch.Recipients.Single(recipient => recipient.Steps.Any(step => step.Id == candidate.StepId));
        var step = recipient.Steps.Single(step => step.Id == candidate.StepId);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (step.NextEvaluationAtUtc > now)
            return null;
        var child = await context.Notifications.IgnoreQueryFilters()
            .SingleOrDefaultAsync(notification => notification.TenantId == candidate.TenantId &&
                notification.DispatchStepId == step.Id && !notification.IsDeleted, cancellationToken);
        var before = step.State;
        if (child is not null)
        {
            if (!dispatch.CanStartWork(now))
            {
                var queued = await context.NotificationDeliveries.IgnoreQueryFilters()
                    .Where(delivery => delivery.TenantId == candidate.TenantId && delivery.NotificationId == child.Id &&
                        (delivery.Status == NotificationStatus.Queued || delivery.Status == NotificationStatus.Failed))
                    .ToListAsync(cancellationToken);
                foreach (var delivery in queued)
                    delivery.StopBeforeSend(now, dispatch.CancelRequestedAtUtc is null);
            }
            var outcome = await ObserveChildAsync(context, child, cancellationToken);
            step.Observe(outcome, "channel_evidence");
            if (!dispatch.CanStartWork(now) && outcome == NotificationDispatchState.Deferred)
                step.Observe(dispatch.CancelRequestedAtUtc.HasValue ? NotificationDispatchState.Cancelled : NotificationDispatchState.Expired,
                    "dispatch_stopped_before_send");
            if (dispatch.Mode == NotificationDispatchMode.OrderedFallback &&
                step.State is NotificationDispatchState.Accepted or NotificationDispatchState.Delivered &&
                recipient.Steps.Any(other => other.Ordinal > step.Ordinal && other.State is not
                    (NotificationDispatchState.Pending or NotificationDispatchState.Suppressed or NotificationDispatchState.Cancelled or NotificationDispatchState.Expired)))
            {
                step.Observe(NotificationDispatchState.NeedsReview, "late_evidence_after_fallback");
                foreach (var other in recipient.Steps.Where(other => other.Ordinal > step.Ordinal))
                    other.Observe(NotificationDispatchState.NeedsReview, "contradictory_delivery_evidence");
            }
        }
        else if (step.State is NotificationDispatchState.Queued or NotificationDispatchState.Accepted ||
            step.State == NotificationDispatchState.Deferred && !step.IsPreparationDeferred)
        {
            step.Observe(NotificationDispatchState.NeedsReview, "channel_evidence_missing");
        }
        else if (!dispatch.CanStartWork(now))
        {
            if (step.State is NotificationDispatchState.Pending or NotificationDispatchState.Scheduling or NotificationDispatchState.Deferred)
                step.Observe(dispatch.CancelRequestedAtUtc.HasValue ? NotificationDispatchState.Cancelled : NotificationDispatchState.Expired,
                    "dispatch_stopped_before_send");
        }
        else if (step.State == NotificationDispatchState.Scheduling && step.ClaimExpiresAtUtc <= now)
        {
            // The child link and queue state commit together. No link means preparation never committed a send.
            step.ReleaseScheduling(now, "preparation_lease_expired");
        }
        if (before != step.State)
            dispatch.Record(NotificationDispatchEventKind.OutcomeChanged, now, recipient.Id, step.Id, step.OutcomeReason);

        SchedulingClaim? claim = null;
        if (child is null && dispatch.CanStartWork(now) && recipient.EligibleSteps(dispatch.Mode, now).Any(eligible => eligible.Id == step.Id))
        {
            var claimId = step.ClaimScheduling(now);
            dispatch.Record(NotificationDispatchEventKind.StepActivated, now, recipient.Id, step.Id);
            claim = new SchedulingClaim(step.Id, dispatch.Id, candidate.TenantId, claimId, step.ContentJson);
        }
        step.ScheduleEvaluation(claim is null ? now.AddSeconds(5) : now.AddSeconds(60));
        RefreshEvaluation(dispatch, now);
        return claim;
    }

    private async Task PrepareAsync(SchedulingClaim claim, CancellationToken cancellationToken)
    {
        NotificationScheduleResult? result = null;
        var outcome = NotificationDispatchState.NeedsReview;
        var reason = "preparation_unavailable";
        DateTime? deferUntil = null;
        try
        {
            var input = JsonSerializer.Deserialize<NotificationChannelRequestHost>(claim.ContentJson, JsonOptions)
                ?? throw new InvalidOperationException("Dispatch channel snapshot is missing.");
            input.SchedulingClaimId = claim.ClaimId;
            using var preparationDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            preparationDeadline.CancelAfter(TimeSpan.FromSeconds(30));
            result = await NotificationContractFactory.ScheduleAsync(scheduler, claim.TenantId, input, preparationDeadline.Token)
                .WaitAsync(preparationDeadline.Token);
            if (result.NotificationId == Guid.Empty)
            {
                outcome = NotificationDispatchState.Suppressed;
                reason = result.SkippedByPreferences ? "user_preference_suppressed" : "tenant_or_audience_suppressed";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (NotificationRateLimitExceededException denied)
        {
            deferUntil = denied.RetryAtUtc;
            reason = "quota_deferred";
        }
        catch (NotificationChannelDisabledByFeatureException)
        {
            outcome = NotificationDispatchState.Suppressed;
            reason = "channel_feature_disabled";
        }
        catch (NotSupportedException)
        {
            outcome = NotificationDispatchState.Suppressed;
            reason = "channel_disabled";
        }
        catch (SharedConflictException) { throw; }
        catch (Exception)
        {
            // Preparation does not send, but unavailable policy/configuration is not authority to bypass it.
            logger.LogWarning("Notification dispatch preparation requires review for step {StepId}.", claim.StepId);
        }
        if (result is { NotificationId: var childId } && childId != Guid.Empty)
            return; // LinkAsync committed the queued step in the child's own transaction.

        await unitOfWorkExecutor.ExecuteTransactionalAsync(async token =>
        {
            var context = await contextProvider.GetDbContextAsync();
            var dispatch = await NotificationDispatchGate.LoadAsync(context, claim.TenantId ?? Guid.Empty, claim.DispatchId, token);
            var recipient = dispatch.Recipients.Single(recipient => recipient.Steps.Any(step => step.Id == claim.StepId));
            var step = recipient.Steps.Single(step => step.Id == claim.StepId);
            var now = timeProvider.GetUtcNow().UtcDateTime;
            if (!step.OwnsSchedulingClaim(claim.ClaimId, now))
                return;
            if (deferUntil.HasValue)
                step.DeferScheduling(deferUntil.Value, reason);
            else
            {
                step.Observe(outcome, reason);
                if (result is { NotificationId: var id } && id == Guid.Empty)
                    foreach (var later in recipient.Steps.Where(other => other.Id != step.Id && other.State is
                        NotificationDispatchState.Pending or NotificationDispatchState.Scheduling or NotificationDispatchState.Deferred))
                        later.Observe(NotificationDispatchState.Suppressed, reason);
            }
            dispatch.Record(NotificationDispatchEventKind.OutcomeChanged, now, recipient.Id, step.Id, reason);
            RefreshEvaluation(dispatch, now);
        }, cancellationToken);
    }

    private static async Task<NotificationDispatchState> ObserveChildAsync(NotificationsDbContextApp context,
        Notification child, CancellationToken cancellationToken)
    {
        var statuses = await context.NotificationDeliveries.IgnoreQueryFilters()
            .Where(delivery => delivery.TenantId == child.TenantId && delivery.NotificationId == child.Id)
            .Select(delivery => new { delivery.Status, delivery.PolicyHeldUntilUtc }).ToListAsync(cancellationToken);
        var digestBuckets = await (from item in context.NotificationDigestItems.IgnoreQueryFilters()
                                   join bucket in context.NotificationDigestBuckets.IgnoreQueryFilters() on item.BucketId equals bucket.Id
                                   where item.TenantId == child.TenantId && bucket.TenantId == child.TenantId && item.SourceNotificationId == child.Id
                                   select new { bucket.Status, bucket.EmittedNotificationId }).ToListAsync(cancellationToken);
        var emitted = digestBuckets.Where(bucket => bucket.EmittedNotificationId.HasValue)
            .Select(bucket => bucket.EmittedNotificationId!.Value).Distinct().ToList();
        if (digestBuckets.Any(bucket => bucket.Status == NotificationDigestBucketStatus.NeedsReview))
            return NotificationDispatchState.NeedsReview;
        if (emitted.Count > 0)
            statuses.AddRange(await context.NotificationDeliveries.IgnoreQueryFilters()
                .Where(delivery => delivery.TenantId == child.TenantId && emitted.Contains(delivery.NotificationId))
                .Select(delivery => new { delivery.Status, delivery.PolicyHeldUntilUtc }).ToListAsync(cancellationToken));
        if (statuses.Any(delivery => delivery.Status == NotificationStatus.NeedsReview))
            return NotificationDispatchState.NeedsReview;
        if (statuses.Any(delivery => delivery.Status == NotificationStatus.Processing))
            return NotificationDispatchState.Queued;
        if (digestBuckets.Any(bucket => bucket.Status is NotificationDigestBucketStatus.Open or NotificationDigestBucketStatus.Processing) ||
            statuses.Any(delivery => delivery.PolicyHeldUntilUtc.HasValue && delivery.Status == NotificationStatus.Queued))
            return NotificationDispatchState.Deferred;
        if (statuses.Any(delivery => delivery.Status is NotificationStatus.Queued or NotificationStatus.Failed))
            return NotificationDispatchState.Queued;
        if (statuses.Count == 0)
            return digestBuckets.Count > 0 ? NotificationDispatchState.Suppressed : NotificationDispatchState.NeedsReview;
        if (statuses.All(delivery => delivery.Status == NotificationStatus.Delivered))
            return NotificationDispatchState.Delivered;
        if (statuses.Any(delivery => delivery.Status is NotificationStatus.ProviderAccepted or NotificationStatus.Delivered))
            return NotificationDispatchState.Accepted;
        if (statuses.All(delivery => delivery.Status == NotificationStatus.Cancelled))
            return NotificationDispatchState.Cancelled;
        if (statuses.All(delivery => delivery.Status is NotificationStatus.Cancelled or NotificationStatus.Expired))
            return NotificationDispatchState.Expired;
        return statuses.All(delivery => delivery.Status == NotificationStatus.DeadLettered)
            ? NotificationDispatchState.Rejected : NotificationDispatchState.NeedsReview;
    }

    private static void RefreshEvaluation(NotificationDispatch dispatch, DateTime nowUtc)
    {
        dispatch.RefreshSummary();
        var active = dispatch.Recipients.SelectMany(recipient => recipient.Steps)
            .Where(step => step.State is NotificationDispatchState.Scheduling or NotificationDispatchState.Queued or NotificationDispatchState.Deferred ||
                !dispatch.CanStartWork(nowUtc) && step.State == NotificationDispatchState.Pending)
            .Concat(dispatch.CanStartWork(nowUtc) ? dispatch.Recipients.SelectMany(recipient => recipient.EligibleSteps(dispatch.Mode, LastUtc)) : [])
            .Select(step => step.NextEvaluationAtUtc).ToArray();
        if (active.Length == 0)
            dispatch.StopPolling();
        else
            dispatch.ScheduleEvaluation(new[] { active.Min(), dispatch.ExpiresAtUtc > nowUtc ? dispatch.ExpiresAtUtc : nowUtc.AddSeconds(5) }.Min());
    }

    private sealed record DueStep(Guid StepId, Guid DispatchId, Guid? TenantId, NotificationDispatchMode Mode, NotificationChannel Channel);
    private sealed record SchedulingClaim(Guid StepId, Guid DispatchId, Guid? TenantId, Guid ClaimId, string ContentJson);
}
