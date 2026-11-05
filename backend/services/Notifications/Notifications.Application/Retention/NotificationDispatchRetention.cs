using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Notifications.Domain.Archival;
using Notifications.Domain.Dispatches;
using Notifications.Domain.Shared.Enums;
using Notifications.EntityFrameworkCore.EntityFrameworkCore;
using Shared.EntityFrameworkCore.TenantDatabases;

namespace Notifications.Application.Retention;

internal static class NotificationDispatchRetention
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IQueryable<NotificationDispatch> Completed(NotificationsDbContextApp context, DateTime cutoffUtc) =>
        context.NotificationDispatches.IgnoreQueryFilters().WhereOwnedByCurrentDatabase()
            .Where(dispatch => dispatch.CreationTime <= cutoffUtc && dispatch.NextEvaluationAtUtc == null &&
                dispatch.State != NotificationDispatchState.NeedsReview && dispatch.State != NotificationDispatchState.Accepted &&
                !context.NotificationDispatchSteps.IgnoreQueryFilters().Any(step => step.ScopeId == dispatch.ScopeId && step.DispatchId == dispatch.Id &&
                    (step.State == NotificationDispatchState.Scheduling || step.State == NotificationDispatchState.Queued ||
                     step.State == NotificationDispatchState.Deferred || step.State == NotificationDispatchState.Accepted || step.State == NotificationDispatchState.NeedsReview)));

    public static async Task ArchiveBatchAsync(NotificationsDbContextApp context, DateTime cutoffUtc, int batchSize,
        DateTime nowUtc, CancellationToken cancellationToken)
    {
        // One bounded graph (<=100 recipients/500 steps) and <=1000 immutable events per database/cycle.
        // A large history drains over several cycles; no history is truncated or buffered without a bound.
        var dispatch = await Completed(context, cutoffUtc).AsNoTracking().Include(dispatch => dispatch.Recipients)
            .ThenInclude(recipient => recipient.Steps)
            .Where(dispatch => !context.Notifications.IgnoreQueryFilters().Any(notification =>
                notification.TenantId == dispatch.TenantId && notification.DispatchStepId != null &&
                context.NotificationDispatchSteps.IgnoreQueryFilters().Any(step => step.ScopeId == dispatch.ScopeId &&
                    step.DispatchId == dispatch.Id && step.Id == notification.DispatchStepId)))
            .OrderBy(dispatch => dispatch.CreationTime).ThenBy(dispatch => dispatch.Id).FirstOrDefaultAsync(cancellationToken);
        if (dispatch is null)
            return;
        // Fence concurrent late evidence/cancellation. This maintenance revision is not a routing event.
        var locked = await context.NotificationDispatches.IgnoreQueryFilters()
            .Where(candidate => candidate.ScopeId == dispatch.ScopeId && candidate.Id == dispatch.Id && candidate.Revision == dispatch.Revision)
            .ExecuteUpdateAsync(update => update.SetProperty(candidate => candidate.Revision, candidate => candidate.Revision + 1), cancellationToken);
        if (locked != 1)
            return;
        var events = await context.NotificationDispatchEvents.IgnoreQueryFilters().AsNoTracking()
            .Where(history => history.ScopeId == dispatch.ScopeId && history.DispatchId == dispatch.Id)
            .OrderBy(history => history.Revision).Take(Math.Clamp(batchSize, 1, 1000)).ToListAsync(cancellationToken);
        foreach (var history in events)
        {
            var payload = JsonSerializer.Serialize(new
            {
                history.Id,
                history.DispatchId,
                history.Revision,
                history.Kind,
                history.OccurredAtUtc,
                history.RecipientId,
                history.StepId,
                history.Reason,
                RedactionApplied = true // Operator identities and all destinations/content are withheld.
            }, JsonOptions);
            context.ArchivedNotificationDispatches.Add(Envelope(dispatch, history.Id, payload, nowUtc));
        }
        await context.SaveChangesAsync(cancellationToken);
        var eventIds = events.Select(history => history.Id).ToArray();
        if (eventIds.Length > 0)
            await context.NotificationDispatchEvents.IgnoreQueryFilters()
                .Where(history => history.ScopeId == dispatch.ScopeId && eventIds.Contains(history.Id)).ExecuteDeleteAsync(cancellationToken);
        if (await context.NotificationDispatchEvents.IgnoreQueryFilters()
            .AnyAsync(history => history.ScopeId == dispatch.ScopeId && history.DispatchId == dispatch.Id, cancellationToken))
            return;
        var header = JsonSerializer.Serialize(new
        {
            dispatch.Id,
            dispatch.RequestId,
            dispatch.TenantId,
            dispatch.Mode,
            dispatch.State,
            dispatch.SourceService,
            dispatch.ActivationTimeUtc,
            dispatch.ExpiresAtUtc,
            dispatch.CancelRequestedAtUtc,
            dispatch.Revision,
            RedactionApplied = true,
            Recipients = dispatch.Recipients.OrderBy(recipient => recipient.Id).Select(recipient => new
            {
                recipient.Id,
                recipient.State,
                Steps = recipient.Steps.OrderBy(step => step.Ordinal).Select(step => new { step.Id, step.Channel, step.Ordinal, step.State, step.OutcomeReason })
            })
        }, JsonOptions);
        context.ArchivedNotificationDispatches.Add(Envelope(dispatch, Guid.Empty, header, nowUtc));
        await context.SaveChangesAsync(cancellationToken);
        await context.NotificationDispatches.IgnoreQueryFilters()
            .Where(candidate => candidate.ScopeId == dispatch.ScopeId && candidate.Id == dispatch.Id && candidate.Revision == dispatch.Revision + 1)
            .ExecuteDeleteAsync(cancellationToken);
    }

    private static ArchivedNotificationDispatch Envelope(NotificationDispatch dispatch, Guid eventId, string payload, DateTime nowUtc) =>
        new(Guid.CreateVersion7(), dispatch.TenantId, dispatch.Id, eventId, nowUtc, payload,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))));
}
