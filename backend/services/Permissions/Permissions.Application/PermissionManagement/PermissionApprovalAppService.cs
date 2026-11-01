using Microsoft.EntityFrameworkCore;
using Permissions.Application.Caching;
using Permissions.Application.Contracts.Common;
using Permissions.Application.Contracts.PermissionApproval;
using Permissions.Domain.Grants;
using Permissions.Domain.Shared.Constants;
using Permissions.Domain.Shared.Enums;
using Permissions.Application.Definitions;
using Shared;
using Shared.AuditLogs.Auditing;
using Shared.CurrentUser;
using Shared.Domain.Entities;
using Shared.Domain.Repositories;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Permissions.Application.PermissionManagement;

/// <summary>
/// Flow 6 approval workflow. Sensitive grants (definition marked <c>RequiresApproval</c>) are recorded
/// here as Pending requests by <see cref="PermissionAssignmentAppService"/> instead of being granted
/// directly; a different admin approves (which applies the grant) or rejects, and the requester may
/// cancel. Authorization is enforced at the controller (Approve permission for approve/reject, grant
/// permission for list/cancel); this service adds the self-approval separation-of-duty guard.
/// </summary>
// No explicit [ExposeServices]: the convention registers both the concrete type (injected by
// PermissionAssignmentAppService for CreatePendingAsync) and the I... interface (used by the controller).
// Not sealed: the concrete type is resolved directly and has [Audited] methods, so the container
// builds a Castle class-proxy around it — which cannot subclass a sealed type.
public class PermissionApprovalAppService : IPermissionApprovalAppService, ITransientDependency
{
    private readonly IRepository<PermissionGrantChangeRequest, Guid> _requestRepository;
    private readonly IRepository<PermissionGrant, Guid> _grantRepository;
    private readonly IRepository<PermissionApprovalDecisionOperation, Guid> _decisionOperationRepository;
    private readonly ICurrentTenant _currentTenant;
    private readonly ICurrentUser _currentUser;
    private readonly PermissionStampService _stampService;
    private readonly PermissionProviderGrantCacheSyncService _grantCacheSync;
    private readonly Permissions.Application.Grants.PermissionGrantHistoryRecorder _historyRecorder;
    private readonly IPendingPermissionGrantValidator _pendingGrantValidator;
    private readonly PermissionDefinitionManager _permissionDefinitions;
    private readonly IPermissionApprovalDisplayProjectionResolver _displayProjections;
    private readonly IPermissionApprovalNotificationPublisher _notifications;

    public PermissionApprovalAppService(
        IRepository<PermissionGrantChangeRequest, Guid> requestRepository,
        IRepository<PermissionGrant, Guid> grantRepository,
        IRepository<PermissionApprovalDecisionOperation, Guid> decisionOperationRepository,
        ICurrentTenant currentTenant,
        ICurrentUser currentUser,
        PermissionStampService stampService,
        PermissionProviderGrantCacheSyncService grantCacheSync,
        Permissions.Application.Grants.PermissionGrantHistoryRecorder historyRecorder,
        IPendingPermissionGrantValidator pendingGrantValidator,
        PermissionDefinitionManager permissionDefinitions,
        IPermissionApprovalDisplayProjectionResolver displayProjections,
        IPermissionApprovalNotificationPublisher notifications)
    {
        _requestRepository = requestRepository;
        _grantRepository = grantRepository;
        _decisionOperationRepository = decisionOperationRepository;
        _currentTenant = currentTenant;
        _currentUser = currentUser;
        _stampService = stampService;
        _grantCacheSync = grantCacheSync;
        _historyRecorder = historyRecorder;
        _pendingGrantValidator = pendingGrantValidator;
        _permissionDefinitions = permissionDefinitions;
        _displayProjections = displayProjections;
        _notifications = notifications;
    }

    /// <summary>
    /// Records a Pending approval request for a sensitive allow grant. Called by the assignment path;
    /// supersedes any earlier Pending request for the same (provider, key, permission) so re-submitting
    /// does not pile up duplicates.
    /// </summary>
    public async Task CreatePendingAsync(
        Guid? tenantId,
        string providerName,
        string providerKey,
        string permissionName,
        DateTime? expiresAtUtc,
        string? reason,
        string? ticketId,
        Guid? requestedByUserId,
        CancellationToken cancellationToken = default)
    {
        var queryable = await _requestRepository.GetQueryableAsync();
        var existingPending = await queryable
            .Where(x => x.TenantId == tenantId &&
                        x.ProviderName == providerName &&
                        x.ProviderKey == providerKey &&
                        x.PermissionName == permissionName &&
                        x.Status == PermissionGrantChangeRequestStatus.Pending)
            .ToListAsync(cancellationToken);

        foreach (var superseded in existingPending)
        {
            superseded.Cancel(requestedByUserId, "Superseded by a newer request", DateTime.UtcNow);
            await _requestRepository.UpdateAsync(superseded, autoSave: false, cancellationToken: cancellationToken);
            await _historyRecorder.RecordAsync(
                tenantId, providerName, providerKey, permissionName,
                PermissionGrantHistoryChangeType.ApprovalCancelled,
                oldEffect: null, newEffect: null,
                expiresAtUtc: null, reason: "Superseded by a newer request", ticketId: null, cancellationToken: cancellationToken);
        }

        var request = new PermissionGrantChangeRequest(
            id: Guid.CreateVersion7(),
            tenantId: tenantId,
            permissionName: permissionName,
            providerName: providerName,
            providerKey: providerKey,
            requestedEffect: PermissionGrantEffect.Allow,
            expiresAtUtc: expiresAtUtc,
            reason: reason,
            ticketId: ticketId,
            requestedByUserId: requestedByUserId,
            createdAtUtc: DateTime.UtcNow);

        await _requestRepository.InsertAsync(request, autoSave: false, cancellationToken: cancellationToken);
        await _historyRecorder.RecordAsync(
            tenantId, providerName, providerKey, permissionName,
            PermissionGrantHistoryChangeType.ApprovalRequested,
            oldEffect: null, newEffect: PermissionGrantEffect.Allow,
            expiresAtUtc: expiresAtUtc, reason: reason, ticketId: ticketId, cancellationToken: cancellationToken);
        await _notifications.RequestedAsync(request, cancellationToken: cancellationToken);
    }

    public async Task<PagedResultDto<PermissionGrantChangeRequestDto>> GetListAsync(
        PermissionGrantChangeRequestListInput? input = null,
        CancellationToken cancellationToken = default)
    {
        input ??= new PermissionGrantChangeRequestListInput();
        var skipCount = Math.Max(0, input.SkipCount);
        var maxResultCount = Math.Clamp(input.MaxResultCount, 1, 1000);

        var queryable = await _requestRepository.GetQueryableAsync();

        // Apply the tab/scope selector.
        switch (input.Scope)
        {
            case PermissionGrantChangeRequestScope.Mine:
                var currentUserId = _currentUser.Id;
                queryable = queryable.Where(x => x.RequestedByUserId == currentUserId);
                break;
            case PermissionGrantChangeRequestScope.History:
                queryable = queryable.Where(x => x.Status != PermissionGrantChangeRequestStatus.Pending);
                break;
            case PermissionGrantChangeRequestScope.All:
                break;
            case PermissionGrantChangeRequestScope.Pending:
            default:
                queryable = queryable.Where(x => x.Status == PermissionGrantChangeRequestStatus.Pending);
                break;
        }

        if (input.Status.HasValue)
        {
            var status = input.Status.Value;
            queryable = queryable.Where(x => x.Status == status);
        }

        var providerName = input.ProviderName?.Trim();
        if (!string.IsNullOrWhiteSpace(providerName))
        {
            queryable = queryable.Where(x => x.ProviderName == providerName);
        }

        var providerKey = input.ProviderKey?.Trim();
        if (!string.IsNullOrWhiteSpace(providerKey))
        {
            queryable = queryable.Where(x => x.ProviderKey == providerKey);
        }

        var permissionName = input.PermissionName?.Trim();
        if (!string.IsNullOrWhiteSpace(permissionName))
        {
            var permissionNamePattern = $"%{permissionName}%";
            queryable = queryable.Where(x => EF.Functions.ILike(x.PermissionName, permissionNamePattern));
        }

        if (input.RequestedByUserId.HasValue)
        {
            var requestedByUserId = input.RequestedByUserId.Value;
            queryable = queryable.Where(x => x.RequestedByUserId == requestedByUserId);
        }

        var totalCount = await queryable.LongCountAsync(cancellationToken);

        // Pending is oldest-first (act on the longest-waiting request first); every other view is
        // newest-first (History/Mine/All order by the most recent decision, then creation).
        queryable = input.Scope == PermissionGrantChangeRequestScope.History
            ? queryable.OrderByDescending(x => x.DecidedAtUtc).ThenByDescending(x => x.CreatedAtUtc)
            : input.Scope == PermissionGrantChangeRequestScope.Pending
                ? queryable.OrderBy(x => x.CreatedAtUtc)
                : queryable.OrderByDescending(x => x.CreatedAtUtc);

        var items = await queryable
            .Skip(skipCount)
            .Take(maxResultCount)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var displayProjections = await _displayProjections.ResolveAsync(items, cancellationToken: cancellationToken);
        return new PagedResultDto<PermissionGrantChangeRequestDto>
        {
            TotalCount = totalCount,
            Items = items.Select(request => MapToDto(request, displayProjections.GetValueOrDefault(request.Id))).ToList()
        };
    }

    public Task<PagedResultDto<PermissionGrantChangeRequestDto>> GetMineAsync(
        PermissionGrantChangeRequestListInput? input = null,
        CancellationToken cancellationToken = default)
    {
        input ??= new PermissionGrantChangeRequestListInput();
        input.Scope = PermissionGrantChangeRequestScope.Mine;
        return GetListAsync(input, cancellationToken: cancellationToken);
    }

    public async Task<PermissionApprovalPendingCountDto> GetPendingCountAsync(
        CancellationToken cancellationToken = default)
    {
        var queryable = await _requestRepository.GetQueryableAsync();
        return new PermissionApprovalPendingCountDto
        {
            Count = await queryable.LongCountAsync(
                x => x.Status == PermissionGrantChangeRequestStatus.Pending,
                cancellationToken)
        };
    }

    [Audited]
    public async Task<PermissionGrantChangeRequestDto> ApproveAsync(
        Guid id,
        DecideGrantChangeRequestInput input,
        CancellationToken cancellationToken = default)
    {
        var replay = await FindSingleDecisionReplayAsync(id, PermissionApprovalDecision.Approve, input?.ExpectedVersion ?? 0, input?.DecisionReason, input?.IdempotencyKey, cancellationToken);
        if (replay != null) return replay;
        var request = await FindPendingAsync(id, cancellationToken: cancellationToken);
        EnsureExpectedVersion(request, input?.ExpectedVersion ?? 0);
        await ApproveCoreAsync(request, RequireDecisionReason(input?.DecisionReason), cancellationToken: cancellationToken);
        var result = await MapToDtoAsync(request, cancellationToken: cancellationToken);
        await StoreSingleDecisionAsync(id, PermissionApprovalDecision.Approve, input!.ExpectedVersion, input.DecisionReason!, input.IdempotencyKey, result, cancellationToken);
        return result;
    }

    [Audited]
    public async Task<PermissionGrantChangeRequestDto> RejectAsync(
        Guid id,
        DecideGrantChangeRequestInput input,
        CancellationToken cancellationToken = default)
    {
        var replay = await FindSingleDecisionReplayAsync(id, PermissionApprovalDecision.Reject, input?.ExpectedVersion ?? 0, input?.DecisionReason, input?.IdempotencyKey, cancellationToken);
        if (replay != null) return replay;
        var request = await FindPendingAsync(id, cancellationToken: cancellationToken);
        EnsureExpectedVersion(request, input?.ExpectedVersion ?? 0);
        await RejectCoreAsync(request, RequireDecisionReason(input?.DecisionReason), cancellationToken: cancellationToken);
        var result = await MapToDtoAsync(request, cancellationToken: cancellationToken);
        await StoreSingleDecisionAsync(id, PermissionApprovalDecision.Reject, input!.ExpectedVersion, input.DecisionReason!, input.IdempotencyKey, result, cancellationToken);
        return result;
    }

    private async Task ApproveCoreAsync(
        PermissionGrantChangeRequest request,
        string decisionReason,
        CancellationToken cancellationToken)
    {
        if (_currentUser.Id.HasValue && request.RequestedByUserId == _currentUser.Id)
        {
            throw new SharedValidationException("You cannot approve your own grant change request.");
        }

        await _pendingGrantValidator.ValidateAsync(request, cancellationToken: cancellationToken);
        await ApplyGrantAsync(request, cancellationToken: cancellationToken);
        request.Approve(_currentUser.Id, decisionReason, DateTime.UtcNow);
        await _historyRecorder.RecordAsync(
            request.TenantId, request.ProviderName, request.ProviderKey, request.PermissionName,
            PermissionGrantHistoryChangeType.Granted,
            oldEffect: null, newEffect: request.RequestedEffect,
            expiresAtUtc: request.ExpiresAtUtc, reason: request.Reason, ticketId: request.TicketId, cancellationToken: cancellationToken);
        await SaveRequestAsync(request, cancellationToken: cancellationToken);
        await _notifications.DecidedAsync(request, cancellationToken: cancellationToken);
    }

    private async Task RejectCoreAsync(
        PermissionGrantChangeRequest request,
        string decisionReason,
        CancellationToken cancellationToken)
    {
        request.Reject(_currentUser.Id, decisionReason, DateTime.UtcNow);
        await _historyRecorder.RecordAsync(
            request.TenantId, request.ProviderName, request.ProviderKey, request.PermissionName,
            PermissionGrantHistoryChangeType.ApprovalRejected,
            oldEffect: null, newEffect: null,
            expiresAtUtc: null, reason: decisionReason, ticketId: request.TicketId, cancellationToken: cancellationToken);
        await SaveRequestAsync(request, cancellationToken: cancellationToken);
        await _notifications.DecidedAsync(request, cancellationToken: cancellationToken);
    }

    [Audited]
    public async Task<PermissionApprovalBulkDecisionResultDto> DecideBulkAsync(
        PermissionApprovalBulkDecisionInput input,
        CancellationToken cancellationToken = default)
    {
        if (!_currentUser.Id.HasValue)
        {
            throw new UnauthorizedAccessException("An authenticated operator is required.");
        }

        if (input?.Items is not { Count: > 0 and <= 100 } ||
            input.Items.Any(item => item.Id == Guid.Empty) ||
            input.Items.Select(item => item.Id).Distinct().Count() != input.Items.Count)
        {
            throw new SharedValidationException("Bulk approval requires 1 to 100 unique request IDs.");
        }

        var operationKey = Check.NotNullOrWhiteSpace(input.IdempotencyKey?.Trim(), nameof(input.IdempotencyKey), 128);
        var requestHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(input))));
        var operationQueryable = await _decisionOperationRepository.GetQueryableAsync();
        var existingOperation = await operationQueryable.AsNoTracking().FirstOrDefaultAsync(
            operation => operation.TenantId == _currentTenant.Id &&
                         operation.ActorUserId == _currentUser.Id.Value &&
                         operation.IdempotencyKey == operationKey,
            cancellationToken: cancellationToken);
        if (existingOperation != null)
        {
            if (!string.Equals(existingOperation.RequestHash, requestHash, StringComparison.Ordinal))
            {
                throw Conflict(PermissionApprovalConflictCode.IdempotencyKeyReused);
            }

            var replay = JsonSerializer.Deserialize<PermissionApprovalBulkDecisionResultDto>(existingOperation.ResultJson)
                ?? throw new InvalidOperationException("Stored approval result is invalid.");
            replay.Replayed = true;
            return replay;
        }

        var decidedRequests = new List<PermissionGrantChangeRequest>(input.Items.Count);
        foreach (var item in input.Items)
        {
            var request = await FindRequestAsync(item.Id, cancellationToken);
            var reason = RequireDecisionReason(item.DecisionReason);

            if (request.Status != PermissionGrantChangeRequestStatus.Pending)
            {
                throw Conflict(PermissionApprovalConflictCode.AlreadyDecided, request);
            }
            EnsureExpectedVersion(request, item.ExpectedVersion);
            switch (item.Decision)
            {
                case PermissionApprovalDecision.Approve:
                    await ApproveCoreAsync(request, reason, cancellationToken: cancellationToken);
                    break;
                case PermissionApprovalDecision.Reject:
                    await RejectCoreAsync(request, reason, cancellationToken: cancellationToken);
                    break;
                default:
                    throw new SharedValidationException($"Unsupported approval decision '{item.Decision}'.");
            }

            decidedRequests.Add(request);
        }

        var projections = await _displayProjections.ResolveAsync(decidedRequests, cancellationToken: cancellationToken);
        var result = new PermissionApprovalBulkDecisionResultDto
        {
            Items = decidedRequests.Select(request => MapToDto(request, projections.GetValueOrDefault(request.Id))).ToList()
        };
        await _decisionOperationRepository.InsertAsync(
            new PermissionApprovalDecisionOperation(
                Guid.CreateVersion7(),
                _currentTenant.Id,
                _currentUser.Id.Value,
                operationKey,
                requestHash,
                JsonSerializer.Serialize(result),
                DateTime.UtcNow),
            autoSave: true,
            cancellationToken: cancellationToken);
        return result;
    }

    [Audited]
    public async Task<PermissionGrantChangeRequestDto> CancelAsync(
        Guid id,
        CancelGrantChangeRequestInput input,
        CancellationToken cancellationToken = default)
    {
        var replay = await FindSingleDecisionReplayAsync(id, PermissionApprovalDecision.Cancel, input?.ExpectedVersion ?? 0, input?.Reason, input?.IdempotencyKey, cancellationToken);
        if (replay != null) return replay;
        var request = await FindPendingAsync(id, cancellationToken: cancellationToken);
        if (!_currentUser.Id.HasValue || request.RequestedByUserId != _currentUser.Id)
        {
            throw new UnauthorizedAccessException("Only the requester can cancel this grant change request.");
        }

        var result = await CancelCoreAsync(request, input!, cancellationToken: cancellationToken);
        await StoreSingleDecisionAsync(id, PermissionApprovalDecision.Cancel, input!.ExpectedVersion, input.Reason!, input.IdempotencyKey, result, cancellationToken);
        return result;
    }

    public async Task<PermissionGrantChangeRequestDto> CancelAnyAsync(
        Guid id,
        CancelGrantChangeRequestInput input,
        CancellationToken cancellationToken = default)
    {
        var request = await FindRequestAsync(id, cancellationToken);
        var replay = await FindSingleDecisionReplayAsync(
            id,
            PermissionApprovalDecision.Cancel,
            input?.ExpectedVersion ?? 0,
            input?.Reason,
            input?.IdempotencyKey,
            cancellationToken);
        if (replay != null)
        {
            return replay;
        }
        if (request.Status != PermissionGrantChangeRequestStatus.Pending)
        {
            throw Conflict(PermissionApprovalConflictCode.AlreadyDecided, request);
        }
        var result = await CancelCoreAsync(request, input!, cancellationToken: cancellationToken);
        await StoreSingleDecisionAsync(
            id,
            PermissionApprovalDecision.Cancel,
            input!.ExpectedVersion,
            input.Reason!,
            input.IdempotencyKey,
            result,
            cancellationToken);
        return result;
    }

    private async Task<PermissionGrantChangeRequestDto> CancelCoreAsync(
        PermissionGrantChangeRequest request,
        CancelGrantChangeRequestInput input,
        CancellationToken cancellationToken)
    {
        EnsureExpectedVersion(request, input?.ExpectedVersion ?? 0);
        var reason = RequireDecisionReason(input?.Reason);
        request.Cancel(_currentUser.Id, reason, DateTime.UtcNow);
        await _historyRecorder.RecordAsync(
            request.TenantId, request.ProviderName, request.ProviderKey, request.PermissionName,
            PermissionGrantHistoryChangeType.ApprovalCancelled,
            oldEffect: null, newEffect: null,
            expiresAtUtc: null, reason: reason, ticketId: request.TicketId, cancellationToken: cancellationToken);
        try
        {
            await _requestRepository.UpdateAsync(request, autoSave: true, cancellationToken: cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw Conflict(PermissionApprovalConflictCode.ConcurrentDecision);
        }
        await _notifications.DecidedAsync(request, cancellationToken: cancellationToken);
        var result = await MapToDtoAsync(request, cancellationToken: cancellationToken);
        return result;
    }

    private static void EnsureExpectedVersion(PermissionGrantChangeRequest request, long expectedVersion)
    {
        if (expectedVersion <= 0 || request.Version != expectedVersion)
        {
            throw Conflict(PermissionApprovalConflictCode.VersionConflict, request);
        }
    }

    private async Task<PermissionGrantChangeRequestDto?> FindSingleDecisionReplayAsync(Guid requestId, PermissionApprovalDecision action, long expectedVersion, string? reason, string? idempotencyKey, CancellationToken cancellationToken)
    {
        if (!_currentUser.Id.HasValue || string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Trim().Length > 128 || expectedVersion <= 0 || string.IsNullOrWhiteSpace(reason))
            throw new SharedValidationException("A bounded idempotency key, expected version, and decision reason are required.");
        var hash = SingleDecisionHash(requestId, action, expectedVersion, reason);
        var queryable = await _decisionOperationRepository.GetQueryableAsync();
        var existing = await queryable.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == _currentTenant.Id && x.ActorUserId == _currentUser.Id.Value && x.IdempotencyKey == idempotencyKey.Trim(), cancellationToken);
        if (existing == null) return null;
        if (!string.Equals(existing.RequestHash, hash, StringComparison.Ordinal)) throw Conflict(PermissionApprovalConflictCode.IdempotencyKeyReused);
        return JsonSerializer.Deserialize<PermissionGrantChangeRequestDto>(existing.ResultJson) ?? throw new SharedValidationException("The stored approval result is invalid.");
    }

    private async Task StoreSingleDecisionAsync(Guid requestId, PermissionApprovalDecision action, long expectedVersion, string reason, string idempotencyKey, PermissionGrantChangeRequestDto result, CancellationToken cancellationToken)
    {
        await _decisionOperationRepository.InsertAsync(new PermissionApprovalDecisionOperation(Guid.CreateVersion7(), _currentTenant.Id, _currentUser.Id!.Value, idempotencyKey.Trim(), SingleDecisionHash(requestId, action, expectedVersion, reason), JsonSerializer.Serialize(result), DateTime.UtcNow), autoSave: true, cancellationToken: cancellationToken);
    }

    private string SingleDecisionHash(Guid requestId, PermissionApprovalDecision action, long expectedVersion, string reason) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{_currentTenant.Id:D}|{requestId:D}|{_currentUser.Id:D}|{(int)action}|{expectedVersion}|{reason.Trim()}")));

    private static string RequireDecisionReason(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new SharedValidationException("A decision reason is required.");
        }

        return Check.NotNullOrWhiteSpace(value.Trim(), nameof(value), PermissionGrantConsts.MaxReasonLength);
    }

    private async Task SaveRequestAsync(
        PermissionGrantChangeRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            await _requestRepository.UpdateAsync(request, autoSave: true, cancellationToken: cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw Conflict(PermissionApprovalConflictCode.ConcurrentDecision);
        }
    }

    private async Task<PermissionGrantChangeRequest> FindPendingAsync(Guid id, CancellationToken cancellationToken)
    {
        var request = await FindRequestAsync(id, cancellationToken);

        if (request.Status != PermissionGrantChangeRequestStatus.Pending)
        {
            throw Conflict(PermissionApprovalConflictCode.AlreadyDecided, request);
        }

        return request;
    }

    private async Task<PermissionGrantChangeRequest> FindRequestAsync(Guid id, CancellationToken cancellationToken) =>
        await _requestRepository.FindAsync(id, cancellationToken: cancellationToken)
        ?? throw new EntityNotFoundException(typeof(PermissionGrantChangeRequest), id);

    private static PermissionApprovalConflictException Conflict(
        PermissionApprovalConflictCode code,
        PermissionGrantChangeRequest? request = null)
        => new(new PermissionApprovalConflictDto
        {
            Code = code,
            CurrentVersion = request?.Version,
            CurrentStatus = request?.Status
        });

    private async Task ApplyGrantAsync(PermissionGrantChangeRequest request, CancellationToken cancellationToken)
    {
        if (request.GrantScopeKind == PermissionGrantScopeKind.Resource)
        {
            throw new SharedValidationException(
                "Resource-scoped grant approvals require an owning resource coordinator, and none is registered.");
        }

        var queryable = await _grantRepository.GetQueryableAsync();
        var existing = await queryable.FirstOrDefaultAsync(
            x => x.TenantId == request.TenantId &&
                 x.ProviderName == request.ProviderName &&
                 x.ProviderKey == request.ProviderKey &&
                 x.Name == request.PermissionName,
            cancellationToken: cancellationToken);

        if (existing != null)
        {
            existing.SetEffect(request.RequestedEffect);
            existing.SetLifecycleMetadata(request.ExpiresAtUtc, request.Reason, request.TicketId, request.DecidedByUserId ?? _currentUser.Id);
            await _grantRepository.UpdateAsync(existing, autoSave: false, cancellationToken: cancellationToken);
        }
        else
        {
            await _grantRepository.InsertAsync(
                new PermissionGrant(
                    id: Guid.CreateVersion7(),
                    tenantId: request.TenantId,
                    name: request.PermissionName,
                    providerName: request.ProviderName,
                    providerKey: request.ProviderKey,
                    effect: request.RequestedEffect,
                    creationTimeUtc: DateTime.UtcNow,
                    expiresAtUtc: request.ExpiresAtUtc,
                    reason: request.Reason,
                    ticketId: request.TicketId,
                    grantedByUserId: _currentUser.Id),
                autoSave: false,
                cancellationToken: cancellationToken);
        }

        var stamp = await _stampService.GetNextStampAsync(request.TenantId, request.ProviderName, request.ProviderKey, cancellationToken: cancellationToken);
        await _grantCacheSync.SyncAsync(request.TenantId, request.ProviderName, request.ProviderKey, stamp, cancellationToken: cancellationToken);
        await _stampService.BumpAsync(request.TenantId, request.ProviderName, request.ProviderKey, stamp, cancellationToken: cancellationToken);
    }


    private async Task<PermissionGrantChangeRequestDto> MapToDtoAsync(
        PermissionGrantChangeRequest request,
        CancellationToken cancellationToken)
    {
        var projections = await _displayProjections.ResolveAsync([request], cancellationToken: cancellationToken);
        return MapToDto(request, projections.GetValueOrDefault(request.Id));
    }

    private PermissionGrantChangeRequestDto MapToDto(
        PermissionGrantChangeRequest request,
        PermissionApprovalDisplayProjection? displayProjection = null)
    {
        return new PermissionGrantChangeRequestDto
        {
            Id = request.Id,
            TenantId = request.TenantId,
            PermissionName = request.PermissionName,
            ProviderName = request.ProviderName,
            ProviderKey = request.ProviderKey,
            RequestedEffect = request.RequestedEffect,
            ExpiresAtUtc = request.ExpiresAtUtc,
            Reason = request.Reason,
            TicketId = request.TicketId,
            RequestedByUserId = request.RequestedByUserId,
            CreatedAtUtc = request.CreatedAtUtc,
            Status = request.Status,
            DecidedByUserId = request.DecidedByUserId,
            DecisionReason = request.DecisionReason,
            DecidedAtUtc = request.DecidedAtUtc,
            Version = request.Version,
            PermissionDisplayName = ResolvePermissionDisplayName(request.PermissionName),
            ProviderDisplayName = displayProjection?.ProviderDisplayName ?? request.ProviderKey,
            RequesterDisplayName = displayProjection?.RequesterDisplayName ?? request.RequestedByUserId?.ToString("D") ?? "System",
            ApproverDisplayName = displayProjection?.ApproverDisplayName ?? request.DecidedByUserId?.ToString("D")
        };
    }

    private string ResolvePermissionDisplayName(string permissionName)
    {
        static IEnumerable<Permissions.Application.Contracts.Definitions.PermissionDefinition> Flatten(
            IEnumerable<Permissions.Application.Contracts.Definitions.PermissionDefinition> definitions)
        {
            foreach (var definition in definitions)
            {
                yield return definition;
                foreach (var child in Flatten(definition.Children))
                {
                    yield return child;
                }
            }
        }

        return _permissionDefinitions.GetGroups()
            .SelectMany(group => Flatten(group.Permissions))
            .FirstOrDefault(definition => string.Equals(definition.Name, permissionName, StringComparison.Ordinal))
            ?.DisplayName ?? permissionName;
    }
}
