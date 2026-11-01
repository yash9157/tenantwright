using Permissions.Application.Caching;
using Permissions.Application.Contracts.PermissionEvaluation;
using Permissions.Application.Definitions;
using Permissions.Application.Evaluation;
using Permissions.Application.Features;
using Permissions.Application.Contracts.Features;
using Permissions.Application.Grants;
using Permissions.Domain.Shared.Constants;
using Permissions.Domain.Shared.Enums;
using Shared.Modularity.DependencyInjection;

namespace Permissions.Application.PermissionEvaluation;

/// <summary>
/// Shared, request-agnostic effective-permission evaluator. HTTP-facing services own caller authorization and
/// tenant-scope checks; internal callers supply authority inputs that were resolved at their owning boundary.
/// </summary>
public sealed class PermissionAuthorizationEvaluator : ITransientDependency
{
    private readonly PermissionSnapshotBuilder _snapshotBuilder;
    private readonly PermissionDecisionEngine _engine;
    private readonly PermissionFeatureChecker _featureChecker;
    private readonly PermissionDefinitionIndex _definitions;
    private readonly IFeatureCheckerClient _features;
    private readonly PermissionConflictRuleService _conflictRules;

    public PermissionAuthorizationEvaluator(
        PermissionSnapshotBuilder snapshotBuilder,
        PermissionDecisionEngine engine,
        PermissionFeatureChecker featureChecker,
        PermissionDefinitionIndex definitions,
        IFeatureCheckerClient features,
        PermissionConflictRuleService conflictRules)
    {
        _snapshotBuilder = snapshotBuilder;
        _engine = engine;
        _featureChecker = featureChecker;
        _definitions = definitions;
        _features = features;
        _conflictRules = conflictRules;
    }

    public async Task<EvaluatePermissionsResultDto> EvaluateAsync(
        Guid? tenantId,
        Guid userId,
        IReadOnlyCollection<Guid> roleIds,
        IReadOnlyCollection<string> permissions,
        CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty)
            throw new global::Shared.SharedValidationException("A valid permission subject is required.");

        var normalizedRoleIds = roleIds
            .Where(roleId => roleId != Guid.Empty)
            .Distinct()
            .OrderBy(roleId => roleId)
            .ToArray();
        var permissionNames = permissions
            .Where(permissionName => !string.IsNullOrWhiteSpace(permissionName))
            .Select(permissionName => permissionName.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (permissionNames.Length == 0 || permissionNames.Any(name => name.Length > PermissionsConsts.MaxPermissionNameLength))
            throw new global::Shared.SharedValidationException("One or more permission names are invalid.");

        var (snapshot, snapshotHash) = await _snapshotBuilder.GetOrBuildAsync(
            tenantId,
            userId,
            normalizedRoleIds,
            cancellationToken);
        var conflictMap = await _conflictRules.BuildConflictMapAsync(tenantId, cancellationToken);

        var results = new List<PermissionEvaluationResultItemDto>(permissionNames.Length);
        foreach (var permissionName in permissionNames)
        {
            if (!_definitions.TryGet(permissionName, out var definition))
            {
                results.Add(new PermissionEvaluationResultItemDto
                {
                    Name = permissionName,
                    IsGranted = false,
                    Source = PermissionDecisionSource.NotDefined
                });
                continue;
            }

            if (!definition.IsEnabled ||
                PermissionPatternHelper.IsWildcard(permissionName) && !definition.AllowsWildcard)
            {
                results.Add(new PermissionEvaluationResultItemDto
                {
                    Name = permissionName,
                    IsGranted = false,
                    Source = PermissionDecisionSource.Disabled
                });
                continue;
            }

            var decision = _engine.Evaluate(snapshot, permissionName, conflictMap);
            results.Add(new PermissionEvaluationResultItemDto
            {
                Name = permissionName,
                IsGranted = decision.IsGranted,
                Source = decision.Source
            });
        }

        var grantedPermissionNames = results
            .Where(result => result.IsGranted)
            .Select(result => result.Name)
            .ToArray();
        var featureMap = await _featureChecker.CheckAsync(tenantId, grantedPermissionNames, cancellationToken);
        var requiredFeatures = results
            .Where(result => result.IsGranted)
            .SelectMany(result => _definitions.TryGet(result.Name, out var definition)
                ? definition.RequiredFeatures
                : [])
            .Where(featureName => !string.IsNullOrWhiteSpace(featureName))
            .Select(featureName => featureName.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var requiredFeatureMap = requiredFeatures.Length == 0
            ? new Dictionary<string, bool>(StringComparer.Ordinal)
            : await _features.AreEnabledAsync(tenantId, requiredFeatures, cancellationToken);

        foreach (var result in results.Where(result => result.IsGranted))
        {
            if (featureMap.TryGetValue(result.Name, out var featureEnabled) && !featureEnabled)
            {
                result.IsGranted = false;
                result.Source = PermissionDecisionSource.FeatureDenied;
                continue;
            }

            if (_definitions.TryGet(result.Name, out var definition) &&
                definition.RequiredFeatures.Any(requiredFeature =>
                    requiredFeatureMap.TryGetValue(requiredFeature.Trim(), out var enabled) && !enabled))
            {
                result.IsGranted = false;
                result.Source = PermissionDecisionSource.FeatureDenied;
            }
        }

        return new EvaluatePermissionsResultDto
        {
            TenantId = tenantId,
            UserId = userId,
            Results = results,
            SnapshotHash = snapshotHash
        };
    }
}
