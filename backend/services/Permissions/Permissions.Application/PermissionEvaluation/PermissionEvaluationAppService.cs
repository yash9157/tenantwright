using Permissions.Application.Contracts.FeatureManagement;
using Permissions.Application.Contracts.PermissionCatalog;
using Permissions.Application.Contracts.PermissionEvaluation;
using Permissions.Application.Contracts.PermissionManagement;
using Permissions.Application.Contracts.ResourcePermissionManagement;
using Permissions.Application.Contracts.TenantPermissionAccess;
using Permissions.Application.Contracts.Authorization;
using Permissions.Application.Contracts.Features;
using Permissions.Application.Contracts.PermissionSnapshots;
using Permissions.Application.Contracts.Provisioning;
using Permissions.Application.Definitions;
using Permissions.Application.Evaluation;
using Permissions.Application.Features;
using Permissions.Application.Grants;
using Permissions.Domain.Shared.Constants;
using Permissions.Domain.Shared.Enums;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;
using Shared.CurrentUser;

using Shared.AuditLogs.Auditing;
namespace Permissions.Application.PermissionEvaluation;

public class PermissionEvaluationAppService : IPermissionEvaluationAppService, ITransientDependency
{
    private readonly IRequestAuthorizationService _authorization;
    private readonly ICurrentTenant _currentTenant;
    private readonly PermissionSnapshotBuilder _snapshotBuilder;
    private readonly PermissionDecisionEngine _engine;
    private readonly PermissionFeatureChecker _featureChecker;
    private readonly PermissionDefinitionIndex _definitions;
    private readonly IFeatureCheckerClient _features;
    private readonly ICurrentUser _currentUser;
    private readonly PermissionConflictRuleService _conflictRules;
    private readonly PermissionAuthorizationEvaluator _authorizationEvaluator;

    public PermissionEvaluationAppService(
        IRequestAuthorizationService authorization,
        ICurrentTenant currentTenant,
        PermissionSnapshotBuilder snapshotBuilder,
        PermissionDecisionEngine engine,
        PermissionFeatureChecker featureChecker,
        PermissionDefinitionIndex definitions,
        IFeatureCheckerClient features,
        ICurrentUser currentUser,
        PermissionConflictRuleService conflictRules,
        PermissionAuthorizationEvaluator authorizationEvaluator)
    {
        _authorization = authorization;
        _currentTenant = currentTenant;
        _snapshotBuilder = snapshotBuilder;
        _engine = engine;
        _featureChecker = featureChecker;
        _definitions = definitions;
        _features = features;
        _currentUser = currentUser;
        _conflictRules = conflictRules;
        _authorizationEvaluator = authorizationEvaluator;
    }


    [Audited]
    public async Task<EvaluatePermissionsResultDto> EvaluateAsync(EvaluatePermissionsRequestDto input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        await _authorization.EnsureAuthorizedAsync(PermissionsPolicies.EvaluatePermissions, cancellationToken);
        return await _authorizationEvaluator.EvaluateAsync(
            ResolveTenantScope(input.TenantId),
            input.UserId,
            input.RoleIds,
            input.Permissions,
            cancellationToken);
    }

    [Audited]
    public async Task<GetMyPermissionsResultDto> GetMyPermissionsAsync(CancellationToken cancellationToken = default)
    {
        if (!_currentUser.IsAuthenticated || !_currentUser.Id.HasValue)
        {
            throw new UnauthorizedAccessException("User must be authenticated to retrieve permissions.");
        }

        var userId = _currentUser.Id.Value;
        var roleIds = GetCurrentIdentityRoleIds();
        var tenantId = _currentUser.TenantId;

        var (snapshot, snapshotHash) = await _snapshotBuilder.GetOrBuildAsync(
            tenantId, userId, roleIds, cancellationToken);
        var conflictMap = await _conflictRules.BuildConflictMapAsync(tenantId, cancellationToken);

        var allPermissionNames = _definitions.GetAllNames();
        var results = new List<PermissionEvaluationResultItemDto>(allPermissionNames.Count);

        foreach (var permissionName in allPermissionNames)
        {
            if (!_definitions.TryGet(permissionName, out var def))
            {
                continue;
            }

            if (!def.IsEnabled)
            {
                continue;
            }

            var decision = _engine.Evaluate(snapshot, permissionName, conflictMap);
            if (decision.IsGranted)
            {
                results.Add(new PermissionEvaluationResultItemDto
                {
                    Name = permissionName,
                    IsGranted = true,
                    Source = decision.Source
                });
            }
        }

        var allowed = results.Select(r => r.Name).ToArray();
        var featureMap = await _featureChecker.CheckAsync(tenantId, allowed, cancellationToken);

        var requiredSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in results)
        {
            if (_definitions.TryGet(item.Name, out var def) && def.RequiredFeatures.Count > 0)
            {
                foreach (var required in def.RequiredFeatures)
                {
                    if (!string.IsNullOrWhiteSpace(required))
                    {
                        requiredSet.Add(required.Trim());
                    }
                }
            }
        }

        var requiredFeatures = requiredSet.ToArray();
        var requiredFeatureMap = requiredFeatures.Length == 0
            ? new Dictionary<string, bool>(StringComparer.Ordinal)
            : await _features.AreEnabledAsync(tenantId, requiredFeatures, cancellationToken);

        var finalPermissions = new List<string>(results.Count);
        foreach (var item in results)
        {
            var isGranted = true;
            if (featureMap.TryGetValue(item.Name, out var enabled) && !enabled)
            {
                isGranted = false;
            }

            if (isGranted && _definitions.TryGet(item.Name, out var def) && def.RequiredFeatures.Count > 0)
            {
                foreach (var required in def.RequiredFeatures)
                {
                    var key = required.Trim();
                    if (requiredFeatureMap.TryGetValue(key, out var ok) && !ok)
                    {
                        isGranted = false;
                        break;
                    }
                }
            }

            if (isGranted)
            {
                finalPermissions.Add(item.Name);
            }
        }

        return new GetMyPermissionsResultDto
        {
            TenantId = tenantId,
            UserId = userId,
            Permissions = finalPermissions
        };
    }

    [Audited]
    public async Task<ExplainPermissionResultDto> ExplainAsync(ExplainPermissionRequestDto input, CancellationToken cancellationToken = default)
    {
        input ??= new ExplainPermissionRequestDto();
        await _authorization.EnsureAuthorizedAsync(PermissionsPolicies.EvaluatePermissions, cancellationToken);
        return await ExplainForPrincipalAsync(
            ResolveTenantScope(input.TenantId),
            input.UserId,
            input.RoleIds,
            NormalizePermissionName(input.PermissionName),
            cancellationToken);
    }

    [Audited]
    public async Task<ExplainPermissionResultDto> ExplainMyPermissionAsync(
        ExplainMyPermissionRequestDto input,
        CancellationToken cancellationToken = default)
    {
        input ??= new ExplainMyPermissionRequestDto();
        if (!_currentUser.IsAuthenticated || !_currentUser.Id.HasValue)
        {
            throw new UnauthorizedAccessException("User must be authenticated to explain their permissions.");
        }

        return await ExplainForPrincipalAsync(
            _currentUser.TenantId,
            _currentUser.Id.Value,
            GetCurrentIdentityRoleIds(),
            NormalizePermissionName(input.PermissionName),
            cancellationToken);
    }

    private async Task<ExplainPermissionResultDto> ExplainForPrincipalAsync(
        Guid? tenantId,
        Guid userId,
        IReadOnlyList<Guid> roleIds,
        string permissionName,
        CancellationToken cancellationToken)
    {
        var (snapshot, snapshotHash) = await _snapshotBuilder.GetOrBuildAsync(
            tenantId, userId, roleIds, cancellationToken);

        if (!_definitions.TryGet(permissionName, out var def))
        {
            return BuildExplainResult(tenantId, userId, permissionName, snapshotHash,
                new PermissionDecision(false, PermissionDecisionSource.NotDefined));
        }

        if (!def.IsEnabled)
        {
            return BuildExplainResult(tenantId, userId, permissionName, snapshotHash, new PermissionDecision(false, PermissionDecisionSource.Disabled));
        }

        if (PermissionPatternHelper.IsWildcard(permissionName) && !def.AllowsWildcard)
        {
            return BuildExplainResult(tenantId, userId, permissionName, snapshotHash, new PermissionDecision(false, PermissionDecisionSource.Disabled));
        }

        var conflictMap = await _conflictRules.BuildConflictMapAsync(tenantId, cancellationToken);
        var decision = _engine.Evaluate(snapshot, permissionName, conflictMap);
        if (decision.IsGranted)
        {
            var featureMap = await _featureChecker.CheckAsync(tenantId, [permissionName], cancellationToken);
            if (featureMap.TryGetValue(permissionName, out var enabled) && !enabled)
            {
                decision = new PermissionDecision(false, PermissionDecisionSource.FeatureDenied, decision.MatchedRule);
            }
            else if (def.RequiredFeatures.Count > 0)
            {
                var requiredFeatureMap = await _features.AreEnabledAsync(tenantId, def.RequiredFeatures.ToArray(), cancellationToken);
                foreach (var required in def.RequiredFeatures)
                {
                    if (requiredFeatureMap.TryGetValue(required.Trim(), out var ok) && !ok)
                    {
                        decision = new PermissionDecision(false, PermissionDecisionSource.FeatureDenied, decision.MatchedRule);
                        break;
                    }
                }
            }
        }

        return BuildExplainResult(tenantId, userId, permissionName, snapshotHash, decision);
    }

    private static string NormalizePermissionName(string? permissionName)
    {
        var normalized = permissionName?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > PermissionsConsts.MaxPermissionNameLength)
        {
            throw new global::Shared.SharedValidationException("A valid permission name is required.");
        }

        return normalized;
    }

    private static ExplainPermissionResultDto BuildExplainResult(
        Guid? tenantId,
        Guid userId,
        string permissionName,
        string snapshotHash,
        PermissionDecision decision)
    {
        var grantStep = new PermissionExplanationStepDto
        {
            Type = PermissionExplanationStepType.GrantDecision,
            Passed = decision.Source is not PermissionDecisionSource.NotDefined and
                not PermissionDecisionSource.Disabled and not PermissionDecisionSource.NotGranted,
            Source = decision.Source,
            SourceLabel = PermissionDecisionSourceLabelFormatter.Format(decision.Source),
            MatchedRule = decision.MatchedRule
        };
        return new ExplainPermissionResultDto
        {
            TenantId = tenantId,
            UserId = userId,
            PermissionName = permissionName,
            IsGranted = decision.IsGranted,
            Source = decision.Source,
            SourceLabel = PermissionDecisionSourceLabelFormatter.Format(decision.Source),
            MatchedRule = decision.MatchedRule,
            SnapshotHash = snapshotHash,
            Steps =
            [
                new PermissionExplanationStepDto
                {
                    Type = PermissionExplanationStepType.Definition,
                    Passed = decision.Source != PermissionDecisionSource.NotDefined,
                    Source = decision.Source == PermissionDecisionSource.NotDefined
                        ? PermissionDecisionSource.NotDefined
                        : PermissionDecisionSource.NotGranted,
                    SourceLabel = decision.Source == PermissionDecisionSource.NotDefined
                        ? PermissionDecisionSourceLabelFormatter.Format(PermissionDecisionSource.NotDefined)
                        : "Permission is registered"
                },
                grantStep,
                new PermissionExplanationStepDto
                {
                    Type = decision.Source == PermissionDecisionSource.FeatureDenied
                        ? PermissionExplanationStepType.Feature
                        : decision.Source == PermissionDecisionSource.ConflictDenied
                            ? PermissionExplanationStepType.Conflict
                            : PermissionExplanationStepType.FinalDecision,
                    Passed = decision.IsGranted,
                    Source = decision.Source,
                    SourceLabel = PermissionDecisionSourceLabelFormatter.Format(decision.Source),
                    MatchedRule = decision.MatchedRule
                }
            ]
        };
    }

    private List<Guid> GetCurrentIdentityRoleIds()
    {
        var claims = _currentUser.FindClaims(Shared.Security.SharedClaimTypes.IdentityRoleId);
        if (!Shared.Security.ClaimValueParser.TryParseCanonicalGuidClaims(
                claims.Select(x => x.Value),
                32,
                out var roleIds))
        {
            throw new UnauthorizedAccessException("The immutable role membership claim set is invalid.");
        }
        return roleIds;
    }

    private Guid? ResolveTenantScope(Guid? requestedTenantId)
    {
        var currentTenantId = _currentTenant.Id;
        if (!currentTenantId.HasValue)
        {
            return requestedTenantId;
        }

        if (requestedTenantId.HasValue && requestedTenantId.Value != currentTenantId.Value)
        {
            throw new UnauthorizedAccessException("Cross-tenant permission evaluation is not allowed.");
        }

        return currentTenantId;
    }
}
