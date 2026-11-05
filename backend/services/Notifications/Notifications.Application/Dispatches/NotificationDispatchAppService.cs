using System.Linq.Expressions;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Notifications.Application.Contracts.Common;
using Notifications.Application.Contracts.Dispatches;
using Notifications.Application.Delivery;
using Notifications.Domain.Dispatches;
using Notifications.Domain.Shared.Constants;
using Notifications.Domain.Shared.Enums;
using Notifications.EntityFrameworkCore.EntityFrameworkCore;
using Shared;
using Shared.AuditLogs.Publishing;
using Shared.EntityFrameworkCore;
using Shared.EntityFrameworkCore.TenantDatabases;
using Shared.Domain.Entities;
using Shared.MultiTenancy;
using Shared.Permissions.Authorization;
using Shared.Permissions.Definitions.Notifications;
using Shared.Security;
using Shared.Uow.Abstractions;

namespace Notifications.Application.Dispatches;

public sealed class NotificationDispatchAppService(
    IUnitOfWorkExecutor unitOfWorkExecutor,
    IDbContextProvider<NotificationsDbContextApp> contextProvider,
    ICurrentTenant currentTenant,
    IPermissionChecker permissionChecker,
    ICurrentPrincipalAccessor principalAccessor,
    IAuditLogWriteRequestPublisher auditPublisher,
    TenantDatabaseFanOut tenantDatabases,
    NotificationDatabaseLocator databaseLocator,
    TimeProvider timeProvider) : INotificationDispatchAppService
{
    private static readonly Expression<Func<NotificationDispatch, NotificationDispatchDto>> Projection = dispatch => new(
        dispatch.Id, dispatch.RequestId, dispatch.TenantId, dispatch.SourceService, dispatch.Mode, dispatch.State,
        dispatch.ActivationTimeUtc, dispatch.ExpiresAtUtc, dispatch.CancelRequestedAtUtc, dispatch.Revision, dispatch.Recipients.Count);

    public async Task<PagedResultDto<NotificationDispatchDto>> GetListAsync(GetNotificationDispatchesInput input,
        bool requireTenant, CancellationToken cancellationToken = default)
    {
        await RequirePermissionAsync(NotificationsPermissions.Query.Read);
        var callerTenant = RequireScope(requireTenant);
        if (input.TenantId == Guid.Empty || callerTenant.HasValue && input.TenantId.HasValue && input.TenantId != callerTenant)
            throw ScopeMismatch();
        var tenantId = callerTenant ?? input.TenantId;
        using var database = tenantDatabases.EnterDatabase(callerTenant.HasValue || !tenantId.HasValue ? null :
            await tenantDatabases.FindTenantDatabaseAsync<NotificationsDbContextApp>(tenantId.Value, cancellationToken));
        return await unitOfWorkExecutor.ExecuteNonTransactionalAsync(async token =>
        {
            var context = await contextProvider.GetDbContextAsync();
            var query = context.NotificationDispatches.IgnoreQueryFilters().WhereOwnedByCurrentDatabase()
                .Where(dispatch => !dispatch.IsDeleted && dispatch.TenantId == tenantId);
            if (input.State.HasValue)
                query = query.Where(dispatch => dispatch.State == input.State.Value);
            if (input.Mode.HasValue)
                query = query.Where(dispatch => dispatch.Mode == input.Mode.Value);
            if (input.RequestId.HasValue)
                query = query.Where(dispatch => dispatch.RequestId == input.RequestId.Value);
            return new PagedResultDto<NotificationDispatchDto>
            {
                TotalCount = await query.LongCountAsync(token),
                Items = await query.AsNoTracking().OrderByDescending(dispatch => dispatch.CreationTime).ThenByDescending(dispatch => dispatch.Id)
                    .Skip(Skip(input)).Take(Take(input)).Select(Projection).ToListAsync(token)
            };
        }, cancellationToken);
    }

    public Task<NotificationDispatchDto> GetAsync(Guid id, bool requireTenant, CancellationToken cancellationToken = default) =>
        ReadAsync(id, requireTenant, async (context, dispatch, token) =>
            await context.NotificationDispatches.IgnoreQueryFilters().Where(candidate => candidate.Id == dispatch.Id && candidate.ScopeId == dispatch.ScopeId)
                .Select(Projection).SingleAsync(token), cancellationToken);

    public Task<PagedResultDto<NotificationDispatchRecipientDto>> GetRecipientsAsync(Guid id, NotificationDispatchPageInput input,
        bool requireTenant, CancellationToken cancellationToken = default) =>
        ReadAsync(id, requireTenant, async (context, dispatch, token) =>
        {
            var sensitive = await permissionChecker.IsGrantedAsync(principalAccessor.Principal, NotificationsPermissions.Query.ReadSensitivePayload, token);
            var query = context.NotificationDispatchRecipients.IgnoreQueryFilters()
                .Where(recipient => recipient.ScopeId == dispatch.ScopeId && recipient.DispatchId == dispatch.Id);
            var count = await query.LongCountAsync(token);
            var recipients = await query.AsNoTracking().Include(recipient => recipient.Steps).OrderBy(recipient => recipient.Id)
                .Skip(Skip(input)).Take(Take(input)).ToListAsync(token);
            var stepIds = recipients.SelectMany(recipient => recipient.Steps).Select(step => step.Id).ToArray();
            var children = await context.Notifications.IgnoreQueryFilters().Where(notification => notification.TenantId == dispatch.TenantId &&
                    notification.DispatchStepId.HasValue && stepIds.Contains(notification.DispatchStepId.Value) && !notification.IsDeleted)
                .Select(notification => new { notification.DispatchStepId, notification.Id }).ToDictionaryAsync(notification => notification.DispatchStepId!.Value, notification => notification.Id, token);
            if (sensitive && recipients.Count > 0)
                await PublishAuditAsync(dispatch, "notifications.dispatch.sensitive-payload.read", "disclosed", token);
            return new PagedResultDto<NotificationDispatchRecipientDto>
            {
                TotalCount = count,
                Items = recipients.Select(recipient => new NotificationDispatchRecipientDto(recipient.Id,
                    sensitive ? recipient.RecipientKey : null, sensitive ? recipient.PreferenceUserId : null, recipient.State, !sensitive,
                    recipient.Steps.OrderBy(step => step.Ordinal).Select(step => new NotificationDispatchStepDto(step.Id, step.Channel,
                        step.Ordinal, step.State, step.OutcomeReason, children.TryGetValue(step.Id, out var child) ? child : null,
                        sensitive ? step.ContentJson : null)).ToList())).ToList()
            };
        }, cancellationToken);

    public Task<PagedResultDto<NotificationDispatchEventDto>> GetHistoryAsync(Guid id, NotificationDispatchPageInput input,
        bool requireTenant, CancellationToken cancellationToken = default) =>
        ReadAsync(id, requireTenant, async (context, dispatch, token) =>
        {
            var sensitive = await permissionChecker.IsGrantedAsync(principalAccessor.Principal, NotificationsPermissions.Query.ReadSensitivePayload, token);
            var query = context.NotificationDispatchEvents.IgnoreQueryFilters()
                .Where(history => history.ScopeId == dispatch.ScopeId && history.DispatchId == dispatch.Id);
            if (sensitive)
                await PublishAuditAsync(dispatch, "notifications.dispatch.sensitive-history.read", "disclosed", token);
            return new PagedResultDto<NotificationDispatchEventDto>
            {
                TotalCount = await query.LongCountAsync(token),
                Items = await query.AsNoTracking().OrderByDescending(history => history.Revision).Skip(Skip(input)).Take(Take(input))
                    .Select(history => new NotificationDispatchEventDto(history.Id, history.Revision, history.Kind, history.OccurredAtUtc,
                        history.RecipientId, history.StepId, history.Reason, sensitive ? history.ActorId : null)).ToListAsync(token)
            };
        }, cancellationToken);

    public async Task<NotificationDispatchDto> CancelAsync(Guid id, bool requireTenant, CancellationToken cancellationToken = default)
    {
        await RequirePermissionAsync(NotificationsPermissions.Dispatches.Cancel);
        var callerTenant = RequireScope(requireTenant);
        var actor = ActorId();
        using var database = databaseLocator.Enter(callerTenant.HasValue ? null : await databaseLocator.FindDispatchDatabaseAsync(id, cancellationToken));
        return await unitOfWorkExecutor.ExecuteTransactionalAsync(async token =>
        {
            var context = await contextProvider.GetDbContextAsync();
            var row = await FindAsync(context, id, callerTenant, token);
            var dispatch = await NotificationDispatchGate.LoadAsync(context, row.ScopeId, id, token);
            if (!dispatch.CancelRequestedAtUtc.HasValue)
            {
                dispatch.RequestCancellation(timeProvider.GetUtcNow().UtcDateTime, actor);
                await PublishAuditAsync(dispatch, "notifications.dispatch.cancel", "requested", token);
            }
            await context.SaveChangesAsync(token);
            return await context.NotificationDispatches.IgnoreQueryFilters().Where(candidate => candidate.Id == id && candidate.ScopeId == dispatch.ScopeId)
                .Select(Projection).SingleAsync(token);
        }, cancellationToken);
    }

    private async Task<TResult> ReadAsync<TResult>(Guid id, bool requireTenant,
        Func<NotificationsDbContextApp, NotificationDispatch, CancellationToken, Task<TResult>> read, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(NotificationsPermissions.Query.Read);
        var callerTenant = RequireScope(requireTenant);
        using var database = databaseLocator.Enter(callerTenant.HasValue ? null : await databaseLocator.FindDispatchDatabaseAsync(id, cancellationToken));
        return await unitOfWorkExecutor.ExecuteNonTransactionalAsync(async token =>
        {
            var context = await contextProvider.GetDbContextAsync();
            return await read(context, await FindAsync(context, id, callerTenant, token), token);
        }, cancellationToken);
    }

    public async Task<NotificationDispatchDto> RetryStepAsync(Guid id, Guid stepId, RetryNotificationDispatchStepInput input,
        bool requireTenant, CancellationToken cancellationToken = default)
    {
        await RequirePermissionAsync(NotificationsPermissions.Deliveries.Resend);
        var callerTenant = RequireScope(requireTenant);
        var actor = ActorId();
        using var database = databaseLocator.Enter(callerTenant.HasValue ? null : await databaseLocator.FindDispatchDatabaseAsync(id, cancellationToken));
        return await unitOfWorkExecutor.ExecuteTransactionalAsync(async token =>
        {
            var context = await contextProvider.GetDbContextAsync();
            var row = await FindAsync(context, id, callerTenant, token);
            var dispatch = await NotificationDispatchGate.LoadAsync(context, row.ScopeId, id, token);
            var recipient = dispatch.Recipients.SingleOrDefault(recipient => recipient.Steps.Any(step => step.Id == stepId))
                ?? throw NotificationDispatchGate.Conflict();
            var step = recipient.Steps.Single(step => step.Id == stepId);
            var now = timeProvider.GetUtcNow().UtcDateTime;
            if (!dispatch.CanStartWork(now) || dispatch.Revision != input.ExpectedRevision ||
                step.State != NotificationDispatchState.NeedsReview ||
                step.OutcomeReason == "contradictory_delivery_evidence" ||
                await context.Notifications.IgnoreQueryFilters().AnyAsync(notification => notification.TenantId == dispatch.TenantId && notification.DispatchStepId == stepId, token))
                throw NotificationDispatchGate.Conflict();
            if (dispatch.Mode == NotificationDispatchMode.OrderedFallback &&
                recipient.Steps.Any(other => other.Id != stepId &&
                    (other.Ordinal < step.Ordinal && other.State is not (NotificationDispatchState.Rejected or NotificationDispatchState.Suppressed) ||
                     other.Ordinal > step.Ordinal && other.State is not (NotificationDispatchState.Pending or NotificationDispatchState.Suppressed or NotificationDispatchState.Cancelled or NotificationDispatchState.Expired))))
                throw NotificationDispatchGate.Conflict();
            step.RetryPreparation(now);
            dispatch.Record(NotificationDispatchEventKind.RecoveryRequested, now, recipient.Id, step.Id, "operator_preparation_retry", actor);
            dispatch.ScheduleEvaluation(now);
            dispatch.RefreshSummary();
            await PublishAuditAsync(dispatch, "notifications.dispatch.step.retry", "requested", token);
            await context.SaveChangesAsync(token);
            return await context.NotificationDispatches.IgnoreQueryFilters().Where(candidate => candidate.ScopeId == dispatch.ScopeId && candidate.Id == id)
                .Select(Projection).SingleAsync(token);
        }, cancellationToken);
    }

    private static async Task<NotificationDispatch> FindAsync(NotificationsDbContextApp context, Guid id, Guid? callerTenant,
        CancellationToken cancellationToken) => await context.NotificationDispatches.IgnoreQueryFilters().WhereOwnedByCurrentDatabase()
        .SingleOrDefaultAsync(dispatch => dispatch.Id == id && !dispatch.IsDeleted &&
            (!callerTenant.HasValue || dispatch.TenantId == callerTenant), cancellationToken)
        ?? throw new EntityNotFoundException(NotificationErrorCodes.DispatchNotFound, "The notification dispatch was not found in this scope.");

    private async Task RequirePermissionAsync(string permission)
    {
        if (!await permissionChecker.IsGrantedAsync(principalAccessor.Principal, permission))
            throw new SharedForbiddenException(NotificationErrorCodes.DispatchPermissionRequired, "The exact notification dispatch permission is required.");
    }

    private Guid? RequireScope(bool requireTenant)
    {
        if (currentTenant.Id == Guid.Empty || requireTenant && !currentTenant.Id.HasValue)
            throw ScopeMismatch();
        return currentTenant.Id;
    }

    private string ActorId() => principalAccessor.Principal.FindFirstValue(SharedClaimTypes.Subject)
        ?? principalAccessor.Principal.FindFirstValue(SharedClaimTypes.UserId)
        ?? throw new SharedForbiddenException(NotificationErrorCodes.DispatchPermissionRequired, "An operator identity is required.");

    private Task PublishAuditAsync(NotificationDispatch dispatch, string category, string result, CancellationToken cancellationToken) =>
        auditPublisher.PublishAsync(new AuditLogWriteRequest
        {
            EventId = Guid.CreateVersion7(),
            TenantId = dispatch.TenantId,
            OccurredAtUtc = timeProvider.GetUtcNow().UtcDateTime,
            SourceService = "notifications-service",
            Category = category,
            UserId = ActorId(),
            StatusCode = 200,
            RedactionApplied = true,
            RedactionVersion = 1,
            Actions = [new AuditLogWriteRequestAction
            {
                Name = category, Result = result,
                Parameters = new Dictionary<string, string?>(StringComparer.Ordinal) { ["dispatchId"] = dispatch.Id.ToString("D") }
            }]
        }, cancellationToken);

    private static int Skip(NotificationDispatchPageInput input) => Math.Max(0, input.SkipCount);
    private static int Take(NotificationDispatchPageInput input) => input.MaxResultCount <= 0 ? 50 : Math.Min(100, input.MaxResultCount);
    private static SharedForbiddenException ScopeMismatch() => new(NotificationErrorCodes.DispatchScopeMismatch, "The dispatch scope must match the current tenant.");
}
