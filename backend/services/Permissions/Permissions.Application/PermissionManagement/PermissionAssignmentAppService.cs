using Microsoft.EntityFrameworkCore;
using Permissions.Application.Caching;
using Permissions.Application.Contracts.Authorization;
using Permissions.Application.Contracts.Definitions;
using Permissions.Application.Contracts.Features;
using Permissions.Application.Contracts.PermissionManagement;
using Permissions.Application.Contracts.PlatformAssignments;
using Permissions.Application.Definitions;
using Permissions.Application.Evaluation;
using Permissions.Application.Features;
using Permissions.Application.Grants;
using Permissions.Application.PlatformAssignments;
using Permissions.Domain.Catalog;
using Permissions.Domain.Grants;
using Permissions.Domain.Shared.Constants;
using Permissions.Domain.Shared.Enums;
using Permissions.EntityFrameworkCore.EntityFrameworkCore;
using Shared;
using Shared.AuditLogs.Auditing;
using Shared.CurrentUser;
using Shared.Data.Exceptions;
using Shared.Domain.Repositories;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;
using Shared.EntityFrameworkCore;
using Shared.Modularity;

namespace Permissions.Application.PermissionManagement;

public class PermissionAssignmentAppService : IPermissionManagementAppService, ITransientDependency
{
    public const string InitialProviderVersion = "00000000-0000-0000-0000-000000000000";
    private static readonly string[] DefaultAllowedProviders =
    {
        PermissionProviderNames.Role,
        PermissionProviderNames.User
    };

    private readonly IRequestAuthorizationService _authorization;
    private readonly ICurrentTenant _currentTenant;
    private readonly ICurrentUser _currentUser;
    private readonly PermissionDefinitionManager _definitionManager;
    private readonly PermissionDefinitionIndex _definitions;
    private readonly PermissionSnapshotBuilder _snapshotBuilder;
    private readonly PermissionDecisionEngine _decisionEngine;
    private readonly PermissionFeatureChecker _featureChecker;
    private readonly IFeatureCheckerClient _features;
    private readonly IRepository<PermissionNode, Guid> _catalogRepository;
    private readonly IRepository<PermissionGrant, Guid> _grantRepository;
    private readonly PermissionProviderGrantCacheSyncService _grantCacheSync;
    private readonly PermissionStampService _stampService;
    private readonly PermissionConflictRuleService _conflictRules;
    private readonly IIdentityPermissionTargetValidator _identityTargets;
    private readonly PermissionApprovalAppService _approvalRequests;
    private readonly PermissionGrantHistoryRecorder _historyRecorder;
    private readonly IDbContextProvider<PermissionsDbContextApp> _dbContextProvider;

    public PermissionAssignmentAppService(
        IRequestAuthorizationService authorization,
        ICurrentTenant currentTenant,
        ICurrentUser currentUser,
        PermissionDefinitionManager definitionManager,
        PermissionDefinitionIndex definitions,
        PermissionSnapshotBuilder snapshotBuilder,
        PermissionDecisionEngine decisionEngine,
        PermissionFeatureChecker featureChecker,
        IFeatureCheckerClient features,
        IRepository<PermissionNode, Guid> catalogRepository,
        IRepository<PermissionGrant, Guid> grantRepository,
        PermissionProviderGrantCacheSyncService grantCacheSync,
        PermissionStampService stampService,
        PermissionConflictRuleService conflictRules,
        IIdentityPermissionTargetValidator identityTargets,
        PermissionApprovalAppService approvalRequests,
        PermissionGrantHistoryRecorder historyRecorder,
        IDbContextProvider<PermissionsDbContextApp> dbContextProvider)
    {
        _authorization = authorization;
        _currentTenant = currentTenant;
        _currentUser = currentUser;
        _definitionManager = definitionManager;
        _definitions = definitions;
        _snapshotBuilder = snapshotBuilder;
        _decisionEngine = decisionEngine;
        _featureChecker = featureChecker;
        _features = features;
        _catalogRepository = catalogRepository;
        _grantRepository = grantRepository;
        _grantCacheSync = grantCacheSync;
        _stampService = stampService;
        _conflictRules = conflictRules;
        _identityTargets = identityTargets;
        _approvalRequests = approvalRequests;
        _historyRecorder = historyRecorder;
        _dbContextProvider = dbContextProvider;
    }

    public async Task<GetPermissionListResultDto> GetAsync(
        string providerName,
        string providerKey,
        CancellationToken cancellationToken = default)
    {
        await _authorization.EnsureAuthorizedAsync(PermissionsPolicies.ManagePermissionGrants, cancellationToken: cancellationToken);
        return await GetProviderPermissionsAsync(providerName, providerKey, cancellationToken: cancellationToken);
    }

    public async Task<GetPermissionListResultDto> GetByGroupAsync(
        string groupName,
        string providerName,
        string providerKey,
        CancellationToken cancellationToken = default)
    {
        await _authorization.EnsureAuthorizedAsync(PermissionsPolicies.ManagePermissionGrants, cancellationToken: cancellationToken);
        return await GetProviderPermissionsAsync(groupName, providerName, providerKey, cancellationToken: cancellationToken);
    }

    [Audited]
    public async Task UpdateAsync(
        string providerName,
        string providerKey,
        UpdatePermissionsDto input,
        CancellationToken cancellationToken = default)
    {
        await _authorization.EnsureAuthorizedAsync(PermissionsPolicies.ManagePermissionGrants, cancellationToken: cancellationToken);
        await UpdateProviderPermissionsAsync(providerName, providerKey, input, cancellationToken: cancellationToken);
    }

    public Task<GetPermissionListResultDto> GetRolePermissionsAsync(
        Guid roleId,
        CancellationToken cancellationToken = default)
    {
        return GetPlatformRolePermissionsAsync(roleId, cancellationToken: cancellationToken);
    }

    [Audited]
    public Task UpdateRolePermissionsAsync(
        Guid roleId,
        UpdatePermissionsDto input,
        CancellationToken cancellationToken = default)
    {
        return UpdatePlatformRolePermissionsAsync(roleId, input, cancellationToken: cancellationToken);
    }

    public Task<GetPermissionListResultDto> GetUserPermissionsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        return GetPlatformUserPermissionsAsync(userId, cancellationToken: cancellationToken);
    }

    public async Task<GetPermissionListResultDto> GetPlatformRolePermissionsAsync(
        Guid roleId,
        CancellationToken cancellationToken = default)
    {
        EnsureHostAssignmentContext();
        var target = await _identityTargets.GetRoleAsync(roleId, tenantId: null, cancellationToken: cancellationToken);
        if (target.IsStatic || !target.IsActive)
        {
            throw new UnauthorizedAccessException("The selected platform role is not assignable.");
        }

        return await GetProviderPermissionsAsync(
            PermissionProviderNames.Role,
            roleId.ToString("D"),
            cancellationToken: cancellationToken);
    }

    public Task<GetPermissionListResultDto> GetPlatformUserPermissionsByIdAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
        => GetPlatformUserPermissionsAsync(userId, cancellationToken: cancellationToken);

    [Audited]
    public Task UpdateUserPermissionsAsync(
        Guid userId,
        UpdatePermissionsDto input,
        CancellationToken cancellationToken = default)
    {
        return UpdatePlatformUserPermissionsAsync(userId, input, cancellationToken: cancellationToken);
    }

    private async Task UpdatePlatformRolePermissionsAsync(
        Guid roleId,
        UpdatePermissionsDto input,
        CancellationToken cancellationToken)
    {
        EnsureHostAssignmentContext();
        var target = await _identityTargets.GetRoleAsync(roleId, tenantId: null, cancellationToken: cancellationToken);
        if (target.IsStatic || !target.IsActive)
        {
            throw new UnauthorizedAccessException("Static platform roles cannot be managed by permission assignment APIs.");
        }

        await UpdateProviderPermissionsAsync(PermissionProviderNames.Role, roleId.ToString("D"), input, cancellationToken: cancellationToken);
    }

    private async Task<GetPermissionListResultDto> GetPlatformUserPermissionsAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        EnsureHostAssignmentContext();
        var target = await _identityTargets.GetUserAsync(userId, tenantId: null, cancellationToken: cancellationToken);
        return await GetUserProviderPermissionsAsync(userId, target.RoleIds, cancellationToken: cancellationToken);
    }

    private async Task UpdatePlatformUserPermissionsAsync(
        Guid userId,
        UpdatePermissionsDto input,
        CancellationToken cancellationToken)
    {
        EnsureHostAssignmentContext();
        await _identityTargets.GetUserAsync(userId, tenantId: null, cancellationToken: cancellationToken);
        await UpdateProviderPermissionsAsync(PermissionProviderNames.User, PermissionAssignmentProviderKeys.NormalizeUserKey(userId), input, cancellationToken: cancellationToken);
    }

    private void EnsureHostAssignmentContext()
    {
        if (_currentTenant.Id.HasValue)
        {
            throw new UnauthorizedAccessException("Platform permission assignment APIs are only available outside tenant context.");
        }
    }

    private Task<GetPermissionListResultDto> GetProviderPermissionsAsync(
        string providerName,
        string providerKey,
        CancellationToken cancellationToken = default)
    {
        return GetProviderPermissionsAsync(groupName: null, providerName, providerKey, cancellationToken: cancellationToken);
    }

    private async Task<GetPermissionListResultDto> GetProviderPermissionsAsync(
        string? groupName,
        string providerName,
        string providerKey,
        CancellationToken cancellationToken = default)
    {
        var tenantId = _currentTenant.Id;
        var normalizedProviderName = PermissionProviderNameNormalizer.Normalize(providerName);
        var normalizedProviderKey = PermissionProviderKeyNormalizer.Normalize(normalizedProviderName, providerKey);

        var grants = await LoadProviderGrantsAsync(tenantId, normalizedProviderName, normalizedProviderKey, cancellationToken: cancellationToken);
        var grantMap = grants.ToDictionary(x => x.Name, x => x, StringComparer.Ordinal);

        var groups = await BuildGroupsAsync(groupName, tenantId, cancellationToken: cancellationToken);
        foreach (var group in groups)
        {
            foreach (var permission in group.Permissions)
            {
                if (grantMap.TryGetValue(permission.Name, out var grant))
                {
                    permission.IsGranted = true;
                    permission.Effect = grant.Effect;
                    permission.GrantedProviders.Add(new GrantedProviderDto
                    {
                        ProviderName = grant.ProviderName,
                        ProviderKey = grant.ProviderKey,
                        Effect = grant.Effect
                    });
                }
            }
        }

        return new GetPermissionListResultDto
        {
            EntityDisplayName = $"{normalizedProviderName}:{normalizedProviderKey}",
            ProviderVersion = await GetProviderVersionAsync(
                tenantId,
                normalizedProviderName,
                normalizedProviderKey,
                cancellationToken),
            Groups = groups
        };
    }

    private async Task<GetPermissionListResultDto> GetUserProviderPermissionsAsync(
        Guid userId,
        IReadOnlyList<Guid>? roleIds,
        CancellationToken cancellationToken)
    {
        var userKey = PermissionAssignmentProviderKeys.NormalizeUserKey(userId);
        var result = await GetProviderPermissionsAsync(PermissionProviderNames.User, userKey, cancellationToken: cancellationToken);
        await ApplyEffectiveUserPermissionsAsync(userId, roleIds ?? [], result.Groups, cancellationToken: cancellationToken);
        return result;
    }

    private async Task ApplyEffectiveUserPermissionsAsync(
        Guid userId,
        IReadOnlyList<Guid> roleIds,
        List<PermissionGroupDto> groups,
        CancellationToken cancellationToken)
    {
        var tenantId = _currentTenant.Id;
        var (snapshot, _) = await _snapshotBuilder.GetOrBuildAsync(tenantId, userId, roleIds, cancellationToken: cancellationToken);
        var permissions = groups.SelectMany(x => x.Permissions).ToList();
        foreach (var permission in permissions)
        {
            var decision = _definitions.TryGet(permission.Name, out var def) && !def.IsEnabled
                ? new PermissionDecision(false, PermissionDecisionSource.Disabled)
                : _decisionEngine.Evaluate(snapshot, permission.Name);

            permission.IsEffectiveGranted = decision.IsGranted;
            permission.EffectiveSource = decision.Source;
            permission.EffectiveSourceLabel = PermissionDecisionSourceLabelFormatter.Format(decision.Source);
        }

        await ApplyEffectiveFeatureDenialsAsync(tenantId, permissions, cancellationToken: cancellationToken);
    }

    private async Task ApplyEffectiveFeatureDenialsAsync(
        Guid? tenantId,
        List<PermissionGrantInfoDto> permissions,
        CancellationToken cancellationToken)
    {
        var granted = permissions.Where(x => x.IsEffectiveGranted == true).ToList();
        if (granted.Count == 0)
        {
            return;
        }

        var featureMap = await _featureChecker.CheckAsync(tenantId, granted.Select(x => x.Name).ToArray(), cancellationToken: cancellationToken);
        var requiredFeatures = granted
            .SelectMany(x => _definitions.TryGet(x.Name, out var def) ? def.RequiredFeatures : [])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var requiredFeatureMap = requiredFeatures.Length == 0
            ? new Dictionary<string, bool>(StringComparer.Ordinal)
            : await _features.AreEnabledAsync(tenantId, requiredFeatures, cancellationToken: cancellationToken);

        foreach (var permission in granted)
        {
            if (featureMap.TryGetValue(permission.Name, out var enabled) && !enabled)
            {
                MarkEffectiveFeatureDenied(permission);
                continue;
            }

            if (!_definitions.TryGet(permission.Name, out var def))
            {
                continue;
            }

            foreach (var required in def.RequiredFeatures)
            {
                var key = required.Trim();
                if (requiredFeatureMap.TryGetValue(key, out var ok) && !ok)
                {
                    MarkEffectiveFeatureDenied(permission);
                    break;
                }
            }
        }
    }

    private static void MarkEffectiveFeatureDenied(PermissionGrantInfoDto permission)
    {
        permission.IsEffectiveGranted = false;
        permission.EffectiveSource = PermissionDecisionSource.FeatureDenied;
        permission.EffectiveSourceLabel = PermissionDecisionSourceLabelFormatter.Format(PermissionDecisionSource.FeatureDenied);
    }

    internal async Task RevokeForAccessReviewAsync(
        Guid tenantId,
        string providerName,
        string providerKey,
        string permissionName,
        string decisionReason,
        string? ticketId,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("An access-review revoke requires an explicit tenant.", nameof(tenantId));
        }

        // Access reviews are host-operated but tenant-owned. Establish the reviewed tenant for the
        // whole normal mutation path so grant lookup, history, stamps, provider cache, and the
        // role-matrix invalidation all use the same selected tenant instead of ambient host scope.
        using var tenantScope = _currentTenant.Change(tenantId);
        var normalizedProviderName = PermissionProviderNameNormalizer.Normalize(providerName);
        var normalizedProviderKey = PermissionProviderKeyNormalizer.Normalize(normalizedProviderName, providerKey);
        await UpdateProviderPermissionsAsync(
            normalizedProviderName,
            normalizedProviderKey,
            new UpdatePermissionsDto
            {
                ExpectedProviderVersion = await GetProviderVersionAsync(
                    tenantId,
                    normalizedProviderName,
                    normalizedProviderKey,
                    cancellationToken),
                Permissions =
                [
                    new UpdatePermissionDto
                    {
                        Name = permissionName,
                        IsGranted = false,
                        Reason = decisionReason,
                        TicketId = ticketId
                    }
                ]
            },
            cancellationToken: cancellationToken);
    }

    internal async Task UpdateTenantProviderPermissionsAsync(
        Guid tenantId,
        string providerName,
        string providerKey,
        UpdatePermissionsDto input,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("A tenant is required.", nameof(tenantId));

        using var tenantScope = _currentTenant.Change(tenantId);
        await UpdateProviderPermissionsAsync(providerName, providerKey, input, cancellationToken);
    }

    private async Task UpdateProviderPermissionsAsync(
        string providerName,
        string providerKey,
        UpdatePermissionsDto input,
        CancellationToken cancellationToken = default)
    {
        var tenantId = _currentTenant.Id;
        var normalizedProviderName = PermissionProviderNameNormalizer.Normalize(providerName);
        var normalizedProviderKey = PermissionProviderKeyNormalizer.Normalize(normalizedProviderName, providerKey);

        var updates = input.Permissions
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .GroupBy(x => x.Name.Trim(), StringComparer.Ordinal)
            .Select(g => g.Last())
            .ToArray();

        if (updates.Length == 0)
        {
            return;
        }

        var dbContext = await _dbContextProvider.GetDbContextAsync();
        var assignmentState = await dbContext.PermissionAssignmentStates
            .SingleOrDefaultAsync(
                state => state.TenantId == tenantId &&
                         state.ProviderName == normalizedProviderName &&
                         state.ProviderKey == normalizedProviderKey,
                cancellationToken: cancellationToken);
        var currentVersion = assignmentState?.Version.ToString("D") ?? InitialProviderVersion;
        if (!string.Equals(input.ExpectedProviderVersion?.Trim(), currentVersion, StringComparison.Ordinal))
        {
            throw Conflict(PermissionAssignmentConflictCode.ProviderVersionConflict, currentVersion);
        }

        var queryable = await _grantRepository.GetQueryableAsync();
        var existingGrants = await queryable
            .Where(x => x.TenantId == tenantId && x.ProviderName == normalizedProviderName &&
                        x.ProviderKey == normalizedProviderKey)
            .ToListAsync(cancellationToken);
        var existingGrantMap = existingGrants.ToDictionary(x => x.Name, StringComparer.Ordinal);

        // The set of permissions this target will effectively hold (Allow effect) after this batch,
        // used purely to detect separation-of-duty conflicts before writing anything. Seeded from
        // existing Allow grants minus any this batch removes/denies, then grown as the batch grants
        // more, so two conflicting permissions submitted in the same request are also caught.
        var effectivelyHeld = new HashSet<string>(
            existingGrants.Where(x => x.Effect == PermissionGrantEffect.Allow).Select(x => x.Name),
            StringComparer.Ordinal);
        foreach (var p in updates)
        {
            var name = p.Name.Trim();
            if (!p.IsGranted || p.Effect != PermissionGrantEffect.Allow)
            {
                effectivelyHeld.Remove(name);
            }
        }

        var conflictMap = await _conflictRules.BuildConflictMapAsync(tenantId, cancellationToken: cancellationToken);

        var hasChanges = false;
        foreach (var p in updates)
        {
            var permissionName = p.Name.Trim();

            if (!_definitions.TryGet(permissionName, out var def))
            {
                throw new ArgumentException($"Unknown permission: {permissionName}", nameof(input));
            }

            if (!def.IsEnabled)
            {
                throw new ArgumentException($"Permission is disabled: {permissionName}", nameof(input));
            }

            if (Evaluation.PermissionPatternHelper.IsWildcard(permissionName) && !def.AllowsWildcard)
            {
                throw new ArgumentException($"Wildcard is not allowed for permission: {permissionName}", nameof(input));
            }

            EnsureProviderAllowed(def, normalizedProviderName);

            if (!p.IsGranted)
            {
                if (existingGrantMap.TryGetValue(permissionName, out var toRemove))
                {
                    await _grantRepository.DeleteAsync(toRemove, autoSave: false, cancellationToken: cancellationToken);
                    await _historyRecorder.RecordAsync(
                        tenantId, normalizedProviderName, normalizedProviderKey, permissionName,
                        PermissionGrantHistoryChangeType.Revoked,
                        oldEffect: toRemove.Effect, newEffect: null,
                        expiresAtUtc: null, reason: p.Reason, ticketId: p.TicketId, cancellationToken: cancellationToken);
                    hasChanges = true;
                }
                continue;
            }

            if (p.Effect == PermissionGrantEffect.Allow)
            {
                var conflict = PermissionConflictRuleService.FindConflict(conflictMap, permissionName, effectivelyHeld);
                if (conflict != null)
                {
                    throw new ArgumentException(
                        $"Cannot grant '{permissionName}': it conflicts with already-held permission '{conflict}' (separation-of-duty rule).",
                        nameof(input));
                }

                effectivelyHeld.Add(permissionName);
            }

            // Flow 6: a *new* allow grant of an approval-gated permission is not applied directly; it
            // becomes a Pending approval request that a different admin must approve. Revokes/denies and
            // lifecycle-only changes to an already-allowed grant still apply directly below.
            if (def.RequiresApproval &&
                p.Effect == PermissionGrantEffect.Allow &&
                (!existingGrantMap.TryGetValue(permissionName, out var currentAllow) || currentAllow.Effect != PermissionGrantEffect.Allow))
            {
                await _approvalRequests.CreatePendingAsync(
                    tenantId,
                    normalizedProviderName,
                    normalizedProviderKey,
                    permissionName,
                    p.ExpiresAtUtc,
                    p.Reason,
                    p.TicketId,
                    _currentUser.Id,
                    cancellationToken: cancellationToken);
                continue;
            }

            var effect = p.Effect;
            var grantedByUserId = _currentUser.Id;
            if (existingGrantMap.TryGetValue(permissionName, out var existing))
            {
                var oldEffect = existing.Effect;
                var lifecycleChanged = existing.ExpiresAtUtc != p.ExpiresAtUtc
                    || !string.Equals(existing.Reason, p.Reason, StringComparison.Ordinal)
                    || !string.Equals(existing.TicketId, p.TicketId, StringComparison.Ordinal);
                var effectChanged = existing.Effect != effect;

                if (effectChanged)
                {
                    existing.SetEffect(effect);
                }

                if (lifecycleChanged)
                {
                    existing.SetLifecycleMetadata(p.ExpiresAtUtc, p.Reason, p.TicketId, grantedByUserId);
                }

                if (effectChanged || lifecycleChanged)
                {
                    await _grantRepository.UpdateAsync(existing, autoSave: false, cancellationToken: cancellationToken);
                    hasChanges = true;
                }

                // History records an effect change (Allow<->Deny). Lifecycle-only metadata edits
                // (reason/ticket/expiry with the same effect) are not treated as a grant-history event.
                if (effectChanged)
                {
                    await _historyRecorder.RecordAsync(
                        tenantId, normalizedProviderName, normalizedProviderKey, permissionName,
                        PermissionGrantHistoryChangeType.EffectChanged,
                        oldEffect: oldEffect, newEffect: effect,
                        expiresAtUtc: p.ExpiresAtUtc, reason: p.Reason, ticketId: p.TicketId, cancellationToken: cancellationToken);
                }
            }
            else
            {
                await _grantRepository.InsertAsync(
                    new PermissionGrant(
                        id: Guid.CreateVersion7(),
                        tenantId: tenantId,
                        name: permissionName,
                        providerName: normalizedProviderName,
                        providerKey: normalizedProviderKey,
                        effect: effect,
                        creationTimeUtc: DateTime.UtcNow,
                        expiresAtUtc: p.ExpiresAtUtc,
                        reason: p.Reason,
                        ticketId: p.TicketId,
                        grantedByUserId: grantedByUserId),
                    autoSave: false,
                    cancellationToken: cancellationToken);

                await _historyRecorder.RecordAsync(
                    tenantId, normalizedProviderName, normalizedProviderKey, permissionName,
                    PermissionGrantHistoryChangeType.Granted,
                    oldEffect: null, newEffect: effect,
                    expiresAtUtc: p.ExpiresAtUtc, reason: p.Reason, ticketId: p.TicketId, cancellationToken: cancellationToken);

                hasChanges = true;
            }
        }

        if (hasChanges)
        {
            if (assignmentState == null)
            {
                assignmentState = new PermissionAssignmentState(
                    Guid.CreateVersion7(),
                    tenantId,
                    normalizedProviderName,
                    normalizedProviderKey,
                    DateTime.UtcNow);
                dbContext.PermissionAssignmentStates.Add(assignmentState);
            }
            else
            {
                assignmentState.Advance(DateTime.UtcNow);
            }

            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (SharedConflictException exception) when (exception.Code is
                SharedErrorCodes.UniqueConstraintViolation or
                SharedErrorCodes.TransactionConflict or
                SharedErrorCodes.ConcurrencyConflict)
            {
                throw Conflict(PermissionAssignmentConflictCode.ConcurrentWriteConflict, currentVersion);
            }
            catch (SharedDbConcurrencyException)
            {
                throw Conflict(PermissionAssignmentConflictCode.ConcurrentWriteConflict, currentVersion);
            }

            var stamp = await _stampService.GetNextStampAsync(tenantId, normalizedProviderName, normalizedProviderKey, cancellationToken: cancellationToken);
            await _grantCacheSync.SyncAsync(tenantId, normalizedProviderName, normalizedProviderKey, stamp, cancellationToken: cancellationToken);
            await _stampService.BumpAsync(tenantId, normalizedProviderName, normalizedProviderKey, stamp, cancellationToken: cancellationToken);
        }
    }

    private async Task<string> GetProviderVersionAsync(
        Guid? tenantId,
        string providerName,
        string providerKey,
        CancellationToken cancellationToken)
    {
        var dbContext = await _dbContextProvider.GetDbContextAsync();
        var version = await dbContext.PermissionAssignmentStates
            .AsNoTracking()
            .Where(state => state.TenantId == tenantId &&
                            state.ProviderName == providerName &&
                            state.ProviderKey == providerKey)
            .Select(state => (Guid?)state.Version)
            .SingleOrDefaultAsync(cancellationToken);
        return version?.ToString("D") ?? InitialProviderVersion;
    }

    private static PlatformPermissionAssignmentConflictException Conflict(
        PermissionAssignmentConflictCode code,
        string? currentVersion = null)
        => new(new PermissionAssignmentConflictDto
        {
            Code = code,
            CurrentProviderVersion = currentVersion
        });

    private async Task<List<PermissionGrant>> LoadProviderGrantsAsync(
        Guid? tenantId,
        string providerName,
        string providerKey,
        CancellationToken cancellationToken)
    {
        var queryable = await _grantRepository.GetQueryableAsync();
        return await queryable
            .Where(x => x.TenantId == tenantId && x.ProviderName == providerName &&
                        x.ProviderKey == providerKey)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
    }

    private async Task<List<PermissionGroupDto>> BuildGroupsAsync(
        string? groupName,
        Guid? tenantId,
        CancellationToken cancellationToken)
    {
        var result = new List<PermissionGroupDto>();

        foreach (var group in _definitionManager.GetGroups())
        {
            if (!string.IsNullOrWhiteSpace(groupName) && !string.Equals(group.Name, groupName, StringComparison.Ordinal))
            {
                continue;
            }

            result.Add(new PermissionGroupDto
            {
                Name = group.Name,
                DisplayName = group.DisplayName ?? group.Name,
                Permissions = Flatten(group.Permissions).Select(ToDto).ToList()
            });
        }

        var catalogPermissions = await LoadCatalogPermissionsAsync(tenantId, cancellationToken: cancellationToken);
        if (catalogPermissions.Count > 0 && (string.IsNullOrWhiteSpace(groupName) || string.Equals(groupName, "Catalog", StringComparison.Ordinal)))
        {
            result.Add(new PermissionGroupDto
            {
                Name = "Catalog",
                DisplayName = "Catalog",
                Permissions = catalogPermissions
            });
        }

        return result;
    }

    private async Task<List<PermissionGrantInfoDto>> LoadCatalogPermissionsAsync(
        Guid? tenantId,
        CancellationToken cancellationToken)
    {
        var queryable = await _catalogRepository.GetQueryableAsync();
        var nodes = await queryable
            .Where(x => x.TenantId == tenantId)
            .AsNoTracking()
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Name)
            .ToListAsync(cancellationToken);

        return nodes.Select(x => new PermissionGrantInfoDto
        {
            Name = x.Name,
            DisplayName = x.DisplayName,
            ParentName = x.ParentName,
            IsGranted = false,
            AllowedProviders = new List<string> { PermissionProviderNames.Role, PermissionProviderNames.User }
        }).ToList();
    }

    private static IEnumerable<PermissionDefinition> Flatten(IEnumerable<PermissionDefinition> definitions)
    {
        foreach (var def in definitions)
        {
            yield return def;
            foreach (var child in Flatten(def.Children))
            {
                yield return child;
            }
        }
    }

    private static PermissionGrantInfoDto ToDto(PermissionDefinition def)
    {
        return new PermissionGrantInfoDto
        {
            Name = def.Name,
            DisplayName = def.DisplayName,
            ParentName = def.ParentName,
            IsGranted = false,
            AllowedProviders = def.Providers.Count == 0
                ? DefaultAllowedProviders.ToList()
                : def.Providers.Select(PermissionProviderNameNormalizer.Normalize).Distinct(StringComparer.Ordinal).ToList()
        };
    }

    private static void EnsureProviderAllowed(PermissionDefinition definition, string providerName)
    {
        var allowedProviders = definition.Providers.Count == 0
            ? DefaultAllowedProviders
            : definition.Providers.Select(PermissionProviderNameNormalizer.Normalize).Distinct(StringComparer.Ordinal).ToArray();

        if (!allowedProviders.Contains(providerName, StringComparer.Ordinal))
        {
            throw new ArgumentException($"Provider '{providerName}' is not allowed for permission '{definition.Name}'.");
        }
    }
}

internal static class PermissionAssignmentProviderKeys
{
    public static string NormalizeUserKey(Guid userId)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User id is required.", nameof(userId));
        }

        return userId.ToString("D");
    }
}
