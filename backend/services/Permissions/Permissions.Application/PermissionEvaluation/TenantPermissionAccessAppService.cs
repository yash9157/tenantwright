using Microsoft.EntityFrameworkCore;
using Permissions.Application.Caching;
using Permissions.Application.Contracts.Authorization;
using Permissions.Application.Contracts.Definitions;
using Permissions.Application.Contracts.FeatureManagement;
using Permissions.Application.Contracts.PermissionCatalog;
using Permissions.Application.Contracts.PermissionEvaluation;
using Permissions.Application.Contracts.PermissionManagement;
using Permissions.Application.Contracts.ResourcePermissionManagement;
using Permissions.Application.Contracts.TenantPermissionAccess;
using Permissions.Application.Contracts.Features;
using Permissions.Application.Contracts.PermissionSnapshots;
using Permissions.Application.Contracts.Provisioning;
using Permissions.Application.Definitions;
using Permissions.Application.Evaluation;
using Permissions.Application.Features;
using Permissions.Domain.Catalog;
using Permissions.Domain.Shared.Constants;
using Permissions.Domain.Shared.Enums;
using Shared.CurrentUser;
using Shared.Domain.Repositories;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;

namespace Permissions.Application.PermissionEvaluation;

public class TenantPermissionAccessAppService : ITenantPermissionAccessAppService, ITransientDependency
{
    private readonly IRequestAuthorizationService _authorization;
    private readonly ICurrentTenant _currentTenant;
    private readonly ICurrentUser _currentUser;
    private readonly PermissionDefinitionManager _definitionManager;
    private readonly PermissionSnapshotBuilder _snapshotBuilder;
    private readonly PermissionDecisionEngine _engine;
    private readonly PermissionFeatureChecker _featureChecker;
    private readonly IFeatureCheckerClient _features;
    private readonly IRepository<PermissionNode, Guid> _catalogRepository;

    public TenantPermissionAccessAppService(
        IRequestAuthorizationService authorization,
        ICurrentTenant currentTenant,
        ICurrentUser currentUser,
        PermissionDefinitionManager definitionManager,
        PermissionSnapshotBuilder snapshotBuilder,
        PermissionDecisionEngine engine,
        PermissionFeatureChecker featureChecker,
        IFeatureCheckerClient features,
        IRepository<PermissionNode, Guid> catalogRepository)
    {
        _authorization = authorization;
        _currentTenant = currentTenant;
        _currentUser = currentUser;
        _definitionManager = definitionManager;
        _snapshotBuilder = snapshotBuilder;
        _engine = engine;
        _featureChecker = featureChecker;
        _features = features;
        _catalogRepository = catalogRepository;
    }

    public async Task<TenantPermissionAccessDto> GetCurrentTenantAccessAsync(GetCurrentTenantPermissionAccessInput input, CancellationToken cancellationToken = default)
    {
        await _authorization.EnsureAuthorizedAsync(PermissionsPolicies.ReadTenantAccess, cancellationToken);
        var tenantId = _currentTenant.Id ?? throw new UnauthorizedAccessException("Tenant permission access requires an active tenant.");
        return await BuildAccessAsync(tenantId, input, cancellationToken);
    }

    /// <summary>
    /// Effective access for the signed-in principal in its current scope. Unlike
    /// <see cref="GetCurrentTenantAccessAsync"/> this does not require an active tenant: a host operator
    /// (no tenant) gets its host-scoped projection. The evaluation stack is already tenant-nullable, so
    /// this only removes the tenant-required guard — the decision semantics are identical.
    /// </summary>
    public async Task<TenantPermissionAccessDto> GetCurrentPrincipalAccessAsync(GetCurrentTenantPermissionAccessInput input, CancellationToken cancellationToken = default)
    {
        await _authorization.EnsureAuthorizedAsync(PermissionsPolicies.ReadTenantAccess, cancellationToken);
        return await BuildAccessAsync(_currentTenant.Id, input, cancellationToken);
    }

    private async Task<TenantPermissionAccessDto> BuildAccessAsync(Guid? tenantId, GetCurrentTenantPermissionAccessInput input, CancellationToken cancellationToken)
    {
        var userId = _currentUser.Id ?? throw new UnauthorizedAccessException("Permission access requires an authenticated user.");
        var roles = _currentUser.Roles
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (!Shared.Security.ClaimValueParser.TryParseCanonicalGuidClaims(
                _currentUser.FindClaims(Shared.Security.SharedClaimTypes.IdentityRoleId).Select(x => x.Value),
                32,
                out var roleIds))
        {
            throw new UnauthorizedAccessException("The immutable role membership claim set is invalid.");
        }

        var (snapshot, snapshotHash) = await _snapshotBuilder.GetOrBuildAsync(tenantId, userId, roleIds, cancellationToken);
        var groups = BuildDefinitionGroups(snapshot);
        groups.AddRange(await BuildCatalogGroupsAsync(tenantId, snapshot, cancellationToken));
        await ApplyFeatureDenialsAsync(tenantId, groups, cancellationToken);

        var filteredGroups = ApplyFilters(groups, input).ToList();
        var allItems = filteredGroups.SelectMany(x => x.Permissions).ToList();

        return new TenantPermissionAccessDto
        {
            TenantId = tenantId,
            UserId = userId,
            UserName = _currentUser.UserName,
            RoleNames = roles.ToList(),
            SnapshotHash = snapshotHash,
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Groups = filteredGroups,
            Summary = new TenantPermissionAccessSummaryDto
            {
                TotalCount = allItems.Count,
                GrantedCount = allItems.Count(x => x.IsGranted),
                DeniedCount = allItems.Count(x => !x.IsGranted),
                FeatureDeniedCount = allItems.Count(x => x.Source == PermissionDecisionSource.FeatureDenied),
                GroupCount = filteredGroups.Count
            }
        };
    }

    private List<TenantPermissionAccessGroupDto> BuildDefinitionGroups(PermissionSnapshotCacheItem snapshot)
    {
        var groups = new List<TenantPermissionAccessGroupDto>();

        foreach (var group in _definitionManager.GetGroups())
        {
            var items = Flatten(group.Permissions)
                .Select(def =>
                {
                    var decision = def.IsEnabled
                        ? _engine.Evaluate(snapshot, def.Name)
                        : new PermissionDecision(false, PermissionDecisionSource.Disabled);

                    return new TenantPermissionAccessItemDto
                    {
                        Name = def.Name,
                        DisplayName = def.DisplayName,
                        ParentName = def.ParentName,
                        IsEnabled = def.IsEnabled,
                        IsGranted = decision.IsGranted,
                        Source = decision.Source,
                        SourceLabel = PermissionDecisionSourceLabelFormatter.Format(decision.Source),
                        ElementType = def.ElementType,
                        ElementKey = def.ElementKey,
                        RequiredFeatures = def.RequiredFeatures.ToList()
                    };
                })
                .ToList();

            groups.Add(BuildGroup(group.Name, group.DisplayName ?? group.Name, items));
        }

        return groups;
    }

    private async Task<List<TenantPermissionAccessGroupDto>> BuildCatalogGroupsAsync(Guid? tenantId, PermissionSnapshotCacheItem snapshot, CancellationToken cancellationToken)
    {
        var queryable = await _catalogRepository.GetQueryableAsync();
        var nodes = await queryable
            .Where(x => x.TenantId == tenantId)
            .AsNoTracking()
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Name)
            .ToListAsync(cancellationToken);

        if (nodes.Count == 0)
        {
            return [];
        }

        var items = nodes.Select(node =>
        {
            var decision = node.IsEnabled
                ? _engine.Evaluate(snapshot, node.Name)
                : new PermissionDecision(false, PermissionDecisionSource.Disabled);

            return new TenantPermissionAccessItemDto
            {
                Name = node.Name,
                DisplayName = node.DisplayName,
                ParentName = node.ParentName,
                IsEnabled = node.IsEnabled,
                IsGranted = decision.IsGranted,
                Source = decision.Source,
                SourceLabel = PermissionDecisionSourceLabelFormatter.Format(decision.Source),
                ElementType = node.ElementType,
                ElementKey = node.ElementKey
            };
        }).ToList();

        return [BuildGroup("Catalog", "Catalog", items)];
    }

    private async Task ApplyFeatureDenialsAsync(Guid? tenantId, List<TenantPermissionAccessGroupDto> groups, CancellationToken cancellationToken)
    {
        var granted = groups.SelectMany(x => x.Permissions).Where(x => x.IsGranted).ToList();
        if (granted.Count == 0)
        {
            return;
        }

        var featureMap = await _featureChecker.CheckAsync(tenantId, granted.Select(x => x.Name).ToArray(), cancellationToken);
        var requiredFeatures = granted
            .SelectMany(x => x.RequiredFeatures)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var requiredFeatureMap = requiredFeatures.Length == 0
            ? new Dictionary<string, bool>(StringComparer.Ordinal)
            : await _features.AreEnabledAsync(tenantId, requiredFeatures, cancellationToken);

        foreach (var item in granted)
        {
            if (featureMap.TryGetValue(item.Name, out var enabled) && !enabled)
            {
                MarkFeatureDenied(item);
                continue;
            }

            foreach (var required in item.RequiredFeatures)
            {
                var key = required.Trim();
                if (requiredFeatureMap.TryGetValue(key, out var ok) && !ok)
                {
                    MarkFeatureDenied(item);
                    break;
                }
            }
        }

        foreach (var group in groups)
        {
            UpdateGroupCounts(group);
        }
    }

    private static IEnumerable<TenantPermissionAccessGroupDto> ApplyFilters(List<TenantPermissionAccessGroupDto> groups, GetCurrentTenantPermissionAccessInput input)
    {
        var filter = input.Filter?.Trim();
        var groupName = input.GroupName?.Trim();

        foreach (var group in groups)
        {
            if (!string.IsNullOrWhiteSpace(groupName) &&
                !string.Equals(group.Name, groupName, StringComparison.Ordinal) &&
                !string.Equals(group.DisplayName, groupName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var permissions = group.Permissions.Where(item =>
                MatchesGrantState(item, input.GrantState) &&
                MatchesFilter(item, filter)).ToList();

            if (permissions.Count == 0)
            {
                continue;
            }

            yield return BuildGroup(group.Name, group.DisplayName, permissions);
        }
    }

    private static bool MatchesGrantState(TenantPermissionAccessItemDto item, TenantPermissionGrantState grantState)
    {
        return grantState switch
        {
            TenantPermissionGrantState.Granted => item.IsGranted,
            TenantPermissionGrantState.Denied => !item.IsGranted,
            _ => true
        };
    }

    private static bool MatchesFilter(TenantPermissionAccessItemDto item, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }

        return Contains(item.Name, filter) ||
               Contains(item.DisplayName, filter) ||
               Contains(item.ParentName, filter) ||
               Contains(item.ElementKey, filter) ||
               Contains(item.SourceLabel, filter);
    }

    private static bool Contains(string? value, string filter)
    {
        return !string.IsNullOrWhiteSpace(value) && value.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private static TenantPermissionAccessGroupDto BuildGroup(string name, string displayName, List<TenantPermissionAccessItemDto> items)
    {
        var group = new TenantPermissionAccessGroupDto
        {
            Name = name,
            DisplayName = displayName,
            Permissions = items
        };

        UpdateGroupCounts(group);
        return group;
    }

    private static void UpdateGroupCounts(TenantPermissionAccessGroupDto group)
    {
        group.TotalCount = group.Permissions.Count;
        group.GrantedCount = group.Permissions.Count(x => x.IsGranted);
        group.DeniedCount = group.Permissions.Count(x => !x.IsGranted);
    }

    private static void MarkFeatureDenied(TenantPermissionAccessItemDto item)
    {
        item.IsGranted = false;
        item.Source = PermissionDecisionSource.FeatureDenied;
        item.SourceLabel = PermissionDecisionSourceLabelFormatter.Format(PermissionDecisionSource.FeatureDenied);
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

}
