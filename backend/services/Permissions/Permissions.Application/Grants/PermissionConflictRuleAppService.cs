using Microsoft.EntityFrameworkCore;
using Permissions.Application.Contracts.Authorization;
using Permissions.Application.Contracts.PermissionConflictRules;
using Permissions.Application.Definitions;
using Permissions.Domain.Grants;
using Permissions.Domain.Shared.Enums;
using Shared.AuditLogs.Auditing;
using Shared.CurrentUser;
using Shared.Domain.Repositories;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;

namespace Permissions.Application.Grants;

/// <summary>
/// Separation-of-duty conflict rules governed by a four-eyes workflow. A create/update/delete is
/// never applied directly; it is recorded as a <see cref="PermissionConflictRuleChangeRequest"/> and a
/// different operator holding <c>Permissions.Management.ConflictRules.Decide</c> approves (applies) or
/// rejects it. Applying is transactional and fails closed on optimistic-concurrency conflicts.
/// </summary>
public class PermissionConflictRuleAppService : IPermissionConflictRuleAppService, ITransientDependency
{
    private const int MaxImpactTargetsReturned = 50;

    private readonly IRequestAuthorizationService _authorization;
    private readonly ICurrentTenant _currentTenant;
    private readonly ICurrentUser _currentUser;
    private readonly IRepository<PermissionConflictRule, Guid> _ruleRepository;
    private readonly IRepository<PermissionConflictRuleChangeRequest, Guid> _requestRepository;
    private readonly IRepository<PermissionGrant, Guid> _grantRepository;
    private readonly PermissionDefinitionIndex _definitions;

    public PermissionConflictRuleAppService(
        IRequestAuthorizationService authorization,
        ICurrentTenant currentTenant,
        ICurrentUser currentUser,
        IRepository<PermissionConflictRule, Guid> ruleRepository,
        IRepository<PermissionConflictRuleChangeRequest, Guid> requestRepository,
        IRepository<PermissionGrant, Guid> grantRepository,
        PermissionDefinitionIndex definitions,
        Permissions.Application.PermissionManagement.IIdentityPermissionTargetValidator identityTargets)
    {
        _authorization = authorization;
        _currentTenant = currentTenant;
        _currentUser = currentUser;
        _ruleRepository = ruleRepository;
        _requestRepository = requestRepository;
        _grantRepository = grantRepository;
        _definitions = definitions;
        _identityTargets = identityTargets;
    }

    private readonly Permissions.Application.PermissionManagement.IIdentityPermissionTargetValidator _identityTargets;

    public async Task<List<PermissionConflictRuleDto>> GetRulesAsync(CancellationToken cancellationToken = default)
    {
        await _authorization.EnsureAuthorizedAsync(PermissionsPolicies.ReadPermissionConflictRules, cancellationToken);

        var tenantId = _currentTenant.Id;
        var queryable = await _ruleRepository.GetQueryableAsync();
        var list = await queryable
            .Where(x => x.TenantId == tenantId)
            .AsNoTracking()
            .OrderBy(x => x.PermissionA)
            .ThenBy(x => x.PermissionB)
            .ToListAsync(cancellationToken);

        return list.Select(ToRuleDto).ToList();
    }

    public async Task<PermissionConflictRuleImpactDto> GetImpactAsync(string permissionA, string permissionB, CancellationToken cancellationToken = default)
    {
        await _authorization.EnsureAuthorizedAsync(PermissionsPolicies.ReadPermissionConflictRules, cancellationToken);

        var (first, second) = NormalizeDefinedPair(permissionA, permissionB);
        var tenantId = _currentTenant.Id;
        var now = DateTime.UtcNow;

        var grants = await _grantRepository.GetQueryableAsync();
        var effective = grants
            .Where(x => x.TenantId == tenantId
                && x.Effect == PermissionGrantEffect.Allow
                && (x.ExpiresAtUtc == null || x.ExpiresAtUtc > now)
                && (x.Name == first || x.Name == second))
            .AsNoTracking();

        // A target violates the pair only when it holds an active allow grant for BOTH permissions.
        var violating = await effective
            .GroupBy(x => new { x.ProviderName, x.ProviderKey })
            .Where(g => g.Any(x => x.Name == first) && g.Any(x => x.Name == second))
            .Select(g => new { g.Key.ProviderName, g.Key.ProviderKey })
            .OrderBy(x => x.ProviderName)
            .ThenBy(x => x.ProviderKey)
            .ToListAsync(cancellationToken);

        return new PermissionConflictRuleImpactDto
        {
            PermissionA = first,
            PermissionB = second,
            ViolatingTargetCount = violating.Count,
            ViolatingTargets = violating
                .Take(MaxImpactTargetsReturned)
                .Select(x => new PermissionConflictRuleImpactTargetDto
                {
                    ProviderName = x.ProviderName,
                    ProviderKey = x.ProviderKey,
                })
                .ToList(),
        };
    }

    public async Task<PagedPermissionConflictRuleChangeRequestsDto> GetChangeRequestsAsync(ConflictRuleChangeRequestListInput input, CancellationToken cancellationToken = default)
    {
        await _authorization.EnsureAuthorizedAsync(PermissionsPolicies.ReadPermissionConflictRules, cancellationToken);

        var tenantId = _currentTenant.Id;
        var queryable = await _requestRepository.GetQueryableAsync();
        var filtered = queryable.Where(x => x.TenantId == tenantId);
        if (input.Status is { } status)
        {
            filtered = filtered.Where(x => x.Status == status);
        }

        var totalCount = await filtered.LongCountAsync(cancellationToken);
        var items = await filtered
            .AsNoTracking()
            .OrderByDescending(x => x.CreatedAtUtc)
            .Skip(input.SkipCount)
            .Take(Math.Clamp(input.MaxResultCount, 1, 200))
            .ToListAsync(cancellationToken);

        var dtos = items.Select(ToRequestDto).ToList();
        await PopulateDisplayNamesAsync(dtos, tenantId, cancellationToken);
        return new PagedPermissionConflictRuleChangeRequestsDto { TotalCount = totalCount, Items = dtos };
    }

    /// <summary>Resolves requester/decider user ids to display names via the Identity target resolver.</summary>
    private async Task PopulateDisplayNamesAsync(List<PermissionConflictRuleChangeRequestDto> dtos, Guid? tenantId, CancellationToken cancellationToken)
    {
        var userIds = dtos
            .SelectMany(x => new[] { x.RequestedByUserId, x.DecidedByUserId })
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
        if (userIds.Count == 0)
        {
            return;
        }

        var references = userIds
            .Select(id => new Permissions.Application.PermissionManagement.IdentityPermissionTargetReference(
                Permissions.Application.PermissionManagement.IdentityPermissionTargetKind.User, id, tenantId))
            .ToList();

        Dictionary<Guid, string> labels;
        try
        {
            var resolved = await _identityTargets.ResolveAsync(references, cancellationToken);
            labels = resolved.ToDictionary(r => r.Id, r => r.DisplayName);
        }
        catch
        {
            // Display names are best-effort; a resolver failure must not break the queue.
            return;
        }

        foreach (var dto in dtos)
        {
            if (dto.RequestedByUserId is { } requester && labels.TryGetValue(requester, out var requesterName))
            {
                dto.RequestedByDisplayName = requesterName;
            }
            if (dto.DecidedByUserId is { } decider && labels.TryGetValue(decider, out var deciderName))
            {
                dto.DecidedByDisplayName = deciderName;
            }
        }
    }

    [Audited]
    public async Task<PermissionConflictRuleChangeRequestDto> RequestCreateAsync(RequestCreatePermissionConflictRuleDto input, CancellationToken cancellationToken = default)
    {
        await _authorization.EnsureAuthorizedAsync(PermissionsPolicies.CreatePermissionConflictRules, cancellationToken);

        var (first, second) = NormalizeDefinedPair(input.PermissionA, input.PermissionB);
        var tenantId = _currentTenant.Id;

        var rules = await _ruleRepository.GetQueryableAsync();
        if (await rules.AnyAsync(x => x.TenantId == tenantId && x.PermissionA == first && x.PermissionB == second, cancellationToken))
        {
            throw Conflict(PermissionConflictRuleChangeConflictCodes.DuplicateRule,
                $"A conflict rule between '{first}' and '{second}' already exists.");
        }

        await EnsureNoPendingCreateForPairAsync(tenantId, first, second, cancellationToken);

        var request = PermissionConflictRuleChangeRequest.ForCreate(
            Guid.CreateVersion7(), tenantId, first, second, input.RuleReason, input.RequestReason, input.TicketId,
            RequireCurrentUserId(), DateTime.UtcNow);
        await _requestRepository.InsertAsync(request, autoSave: true, cancellationToken: cancellationToken);
        return ToRequestDto(request);
    }

    [Audited]
    public async Task<PermissionConflictRuleChangeRequestDto> RequestUpdateAsync(Guid ruleId, RequestUpdatePermissionConflictRuleDto input, CancellationToken cancellationToken = default)
    {
        await _authorization.EnsureAuthorizedAsync(PermissionsPolicies.UpdatePermissionConflictRules, cancellationToken);

        var (first, second) = NormalizeDefinedPair(input.PermissionA, input.PermissionB);
        var tenantId = _currentTenant.Id;
        var rule = await FindRuleAsync(ruleId, tenantId, cancellationToken);
        EnsureExpectedRuleVersion(rule, input.ExpectedRuleVersion);
        await EnsureNoPendingRequestForRuleAsync(ruleId, cancellationToken);

        var request = PermissionConflictRuleChangeRequest.ForUpdate(
            Guid.CreateVersion7(), tenantId, ruleId, input.ExpectedRuleVersion, first, second, input.RuleReason,
            input.RequestReason, input.TicketId, RequireCurrentUserId(), DateTime.UtcNow);
        await _requestRepository.InsertAsync(request, autoSave: true, cancellationToken: cancellationToken);
        return ToRequestDto(request);
    }

    [Audited]
    public async Task<PermissionConflictRuleChangeRequestDto> RequestDeleteAsync(Guid ruleId, RequestDeletePermissionConflictRuleDto input, CancellationToken cancellationToken = default)
    {
        await _authorization.EnsureAuthorizedAsync(PermissionsPolicies.DeletePermissionConflictRules, cancellationToken);

        var tenantId = _currentTenant.Id;
        var rule = await FindRuleAsync(ruleId, tenantId, cancellationToken);
        EnsureExpectedRuleVersion(rule, input.ExpectedRuleVersion);
        await EnsureNoPendingRequestForRuleAsync(ruleId, cancellationToken);

        var request = PermissionConflictRuleChangeRequest.ForDelete(
            Guid.CreateVersion7(), tenantId, ruleId, input.ExpectedRuleVersion, rule.PermissionA, rule.PermissionB,
            input.RequestReason, input.TicketId, RequireCurrentUserId(), DateTime.UtcNow);
        await _requestRepository.InsertAsync(request, autoSave: true, cancellationToken: cancellationToken);
        return ToRequestDto(request);
    }

    [Audited]
    public async Task<PermissionConflictRuleChangeRequestDto> ApproveAsync(Guid changeRequestId, DecidePermissionConflictRuleChangeDto input, CancellationToken cancellationToken = default)
    {
        await _authorization.EnsureAuthorizedAsync(PermissionsPolicies.DecidePermissionConflictRules, cancellationToken);

        var request = await LoadDecidableRequestAsync(changeRequestId, input.ExpectedRequestVersion, cancellationToken);
        var deciderId = RequireCurrentUserId();
        var now = DateTime.UtcNow;

        await ApplyApprovedChangeAsync(request, now, cancellationToken);
        request.Approve(deciderId, input.DecisionReason, now);
        await _requestRepository.UpdateAsync(request, autoSave: true, cancellationToken: cancellationToken);
        return ToRequestDto(request);
    }

    [Audited]
    public async Task<PermissionConflictRuleChangeRequestDto> RejectAsync(Guid changeRequestId, DecidePermissionConflictRuleChangeDto input, CancellationToken cancellationToken = default)
    {
        await _authorization.EnsureAuthorizedAsync(PermissionsPolicies.DecidePermissionConflictRules, cancellationToken);

        var request = await LoadDecidableRequestAsync(changeRequestId, input.ExpectedRequestVersion, cancellationToken);
        request.Reject(RequireCurrentUserId(), input.DecisionReason, DateTime.UtcNow);
        await _requestRepository.UpdateAsync(request, autoSave: true, cancellationToken: cancellationToken);
        return ToRequestDto(request);
    }

    [Audited]
    public async Task CancelAsync(Guid changeRequestId, CancellationToken cancellationToken = default)
    {
        await _authorization.EnsureAuthorizedAsync(PermissionsPolicies.ReadPermissionConflictRules, cancellationToken);

        var tenantId = _currentTenant.Id;
        var request = await _requestRepository.FindAsync(x => x.Id == changeRequestId && x.TenantId == tenantId, cancellationToken: cancellationToken)
            ?? throw Conflict(PermissionConflictRuleChangeConflictCodes.RequestAlreadyDecided, "Conflict-rule change request not found.");

        // Cancel is a requester self-withdrawal; the aggregate enforces ownership and pending state.
        request.Cancel(RequireCurrentUserId(), reason: null, DateTime.UtcNow);
        await _requestRepository.UpdateAsync(request, autoSave: true, cancellationToken: cancellationToken);
    }

    private async Task ApplyApprovedChangeAsync(PermissionConflictRuleChangeRequest request, DateTime now, CancellationToken cancellationToken)
    {
        var tenantId = _currentTenant.Id;

        switch (request.ChangeType)
        {
            case PermissionConflictRuleChangeType.Create:
                {
                    var rules = await _ruleRepository.GetQueryableAsync();
                    if (await rules.AnyAsync(x => x.TenantId == tenantId && x.PermissionA == request.PermissionA && x.PermissionB == request.PermissionB, cancellationToken))
                    {
                        throw Conflict(PermissionConflictRuleChangeConflictCodes.DuplicateRule,
                            $"A conflict rule between '{request.PermissionA}' and '{request.PermissionB}' already exists.");
                    }

                    var rule = new PermissionConflictRule(Guid.CreateVersion7(), tenantId, request.PermissionA, request.PermissionB, now, request.RuleReason);
                    await _ruleRepository.InsertAsync(rule, autoSave: true, cancellationToken: cancellationToken);
                    break;
                }

            case PermissionConflictRuleChangeType.Update:
                {
                    var rule = await FindRuleAsync(request.TargetRuleId!.Value, tenantId, cancellationToken);
                    EnsureExpectedRuleVersion(rule, request.ExpectedRuleVersion!.Value);
                    rule.Update(request.PermissionA, request.PermissionB, request.RuleReason, now);
                    await _ruleRepository.UpdateAsync(rule, autoSave: true, cancellationToken: cancellationToken);
                    break;
                }

            case PermissionConflictRuleChangeType.Delete:
                {
                    var rule = await FindRuleAsync(request.TargetRuleId!.Value, tenantId, cancellationToken);
                    EnsureExpectedRuleVersion(rule, request.ExpectedRuleVersion!.Value);
                    await _ruleRepository.DeleteAsync(rule, autoSave: true, cancellationToken: cancellationToken);
                    break;
                }

            default:
                throw new InvalidOperationException($"Unsupported conflict-rule change type: {request.ChangeType}.");
        }
    }

    private async Task<PermissionConflictRuleChangeRequest> LoadDecidableRequestAsync(Guid changeRequestId, long expectedRequestVersion, CancellationToken cancellationToken)
    {
        var tenantId = _currentTenant.Id;
        var request = await _requestRepository.FindAsync(x => x.Id == changeRequestId && x.TenantId == tenantId, cancellationToken: cancellationToken)
            ?? throw Conflict(PermissionConflictRuleChangeConflictCodes.RequestAlreadyDecided, "Conflict-rule change request not found.");

        if (request.Status != PermissionConflictRuleChangeStatus.Pending)
        {
            throw Conflict(PermissionConflictRuleChangeConflictCodes.RequestAlreadyDecided,
                $"Conflict-rule change request is already {request.Status}.", request.Version);
        }

        if (request.Version != expectedRequestVersion)
        {
            throw Conflict(PermissionConflictRuleChangeConflictCodes.RequestVersionMismatch,
                "Conflict-rule change request changed since it was loaded.", request.Version);
        }

        return request;
    }

    private async Task<PermissionConflictRule> FindRuleAsync(Guid ruleId, Guid? tenantId, CancellationToken cancellationToken)
    {
        return await _ruleRepository.FindAsync(x => x.Id == ruleId && x.TenantId == tenantId, cancellationToken: cancellationToken)
            ?? throw Conflict(PermissionConflictRuleChangeConflictCodes.RuleNotFound, "Conflict rule not found.");
    }

    private async Task EnsureNoPendingCreateForPairAsync(Guid? tenantId, string first, string second, CancellationToken cancellationToken)
    {
        var requests = await _requestRepository.GetQueryableAsync();
        var hasPending = await requests.AnyAsync(x => x.TenantId == tenantId
            && x.Status == PermissionConflictRuleChangeStatus.Pending
            && x.ChangeType == PermissionConflictRuleChangeType.Create
            && x.PermissionA == first && x.PermissionB == second, cancellationToken);
        if (hasPending)
        {
            throw Conflict(PermissionConflictRuleChangeConflictCodes.PendingRequestExists,
                "A pending create request already exists for this pair.");
        }
    }

    private async Task EnsureNoPendingRequestForRuleAsync(Guid ruleId, CancellationToken cancellationToken)
    {
        var requests = await _requestRepository.GetQueryableAsync();
        var hasPending = await requests.AnyAsync(x => x.TargetRuleId == ruleId
            && x.Status == PermissionConflictRuleChangeStatus.Pending, cancellationToken);
        if (hasPending)
        {
            throw Conflict(PermissionConflictRuleChangeConflictCodes.PendingRequestExists,
                "A pending change request already targets this rule.");
        }
    }

    private static void EnsureExpectedRuleVersion(PermissionConflictRule rule, long expectedVersion)
    {
        if (rule.Version != expectedVersion)
        {
            throw Conflict(PermissionConflictRuleChangeConflictCodes.RuleVersionMismatch,
                "Conflict rule changed since it was loaded.", rule.Version);
        }
    }

    private (string First, string Second) NormalizeDefinedPair(string permissionA, string permissionB)
    {
        var a = permissionA?.Trim() ?? throw new ArgumentException("PermissionA is required.", nameof(permissionA));
        var b = permissionB?.Trim() ?? throw new ArgumentException("PermissionB is required.", nameof(permissionB));

        if (!_definitions.IsDefined(a))
        {
            throw new ArgumentException($"Unknown permission: {a}", nameof(permissionA));
        }

        if (!_definitions.IsDefined(b))
        {
            throw new ArgumentException($"Unknown permission: {b}", nameof(permissionB));
        }

        return PermissionConflictRuleKeyHelper.NormalizePair(a, b);
    }

    private Guid RequireCurrentUserId()
    {
        return _currentUser.Id ?? throw new InvalidOperationException("An authenticated user is required for conflict-rule changes.");
    }

    private static PermissionConflictRuleChangeConflictException Conflict(string code, string message, long? currentVersion = null)
    {
        return new PermissionConflictRuleChangeConflictException(new PermissionConflictRuleChangeConflictDto
        {
            Code = code,
            Message = message,
            CurrentVersion = currentVersion,
        });
    }

    private static PermissionConflictRuleDto ToRuleDto(PermissionConflictRule rule) => new()
    {
        Id = rule.Id,
        PermissionA = rule.PermissionA,
        PermissionB = rule.PermissionB,
        Reason = rule.Reason,
        CreationTimeUtc = rule.CreationTimeUtc,
        LastModifiedTimeUtc = rule.LastModifiedTimeUtc,
        Version = rule.Version,
    };

    private static PermissionConflictRuleChangeRequestDto ToRequestDto(PermissionConflictRuleChangeRequest request) => new()
    {
        Id = request.Id,
        ChangeType = request.ChangeType,
        TargetRuleId = request.TargetRuleId,
        ExpectedRuleVersion = request.ExpectedRuleVersion,
        PermissionA = request.PermissionA,
        PermissionB = request.PermissionB,
        RuleReason = request.RuleReason,
        RequestReason = request.RequestReason,
        TicketId = request.TicketId,
        RequestedByUserId = request.RequestedByUserId,
        CreatedAtUtc = request.CreatedAtUtc,
        Status = request.Status,
        DecidedByUserId = request.DecidedByUserId,
        DecisionReason = request.DecisionReason,
        DecidedAtUtc = request.DecidedAtUtc,
        Version = request.Version,
    };
}
