using Microsoft.EntityFrameworkCore;
using Permissions.Application.Contracts.Authorization;
using Permissions.Application.Contracts.FeatureManagement;
using Permissions.Application.Contracts.PermissionCatalog;
using Permissions.Application.Contracts.PermissionEvaluation;
using Permissions.Application.Contracts.PermissionManagement;
using Permissions.Application.Contracts.ResourcePermissionManagement;
using Permissions.Application.Contracts.TenantPermissionAccess;
using Permissions.Application.Contracts.Features;
using Permissions.Application.Contracts.PermissionSnapshots;
using Permissions.Application.Contracts.Provisioning;
using Permissions.Application.Caching;
using Permissions.Application.Definitions;
using Permissions.Application.Evaluation;
using Permissions.Domain.Grants;
using Permissions.Domain.Shared.Constants;
using Permissions.Domain.Shared.Enums;
using Shared.Domain.Repositories;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;

using Shared.AuditLogs.Auditing;
namespace Permissions.Application.PermissionManagement;

public class ResourcePermissionManagementAppService : IResourcePermissionManagementAppService, ITransientDependency
{
    private static readonly string[] DefaultAllowedProviders =
    {
        PermissionProviderNames.Role,
        PermissionProviderNames.User
    };

    private readonly IRequestAuthorizationService _authorization;
    private readonly ICurrentTenant _currentTenant;
    private readonly PermissionDefinitionManager _definitionManager;
    private readonly PermissionDefinitionIndex _definitions;
    private readonly IRepository<ResourcePermissionGrant, Guid> _repository;
    private readonly PermissionStampService _stampService;

    public ResourcePermissionManagementAppService(
        IRequestAuthorizationService authorization,
        ICurrentTenant currentTenant,
        PermissionDefinitionManager definitionManager,
        PermissionDefinitionIndex definitions,
        IRepository<ResourcePermissionGrant, Guid> repository,
        PermissionStampService stampService)
    {
        _authorization = authorization;
        _currentTenant = currentTenant;
        _definitionManager = definitionManager;
        _definitions = definitions;
        _repository = repository;
        _stampService = stampService;
    }

    public async Task<GetResourcePermissionListResultDto> GetAsync(string resourceName, string resourceKey, string providerName, string providerKey, CancellationToken cancellationToken = default)
    {
        await _authorization.EnsureAuthorizedAsync(PermissionsPolicies.ManagePermissionGrants, cancellationToken);
        var tenantId = _currentTenant.Id;
        resourceName = resourceName.Trim();
        resourceKey = resourceKey.Trim();
        providerName = PermissionProviderNameNormalizer.Normalize(providerName);
        providerKey = PermissionProviderKeyNormalizer.Normalize(providerName, providerKey);

        var queryable = await _repository.GetQueryableAsync();
        var grants = await queryable
            .Where(x => x.TenantId == tenantId &&
                        x.ResourceName == resourceName &&
                        x.ResourceKey == resourceKey &&
                        x.ProviderName == providerName &&
                        x.ProviderKey == providerKey)
            .AsNoTracking()
            .Select(x => new { x.Name, x.Effect })
            .ToListAsync(cancellationToken);

        // A stored Deny row means "this resource grant explicitly revokes the permission"; it must not
        // be shown/counted as granted even though a row exists. Allow-and-Deny rows cannot coexist for
        // the same (resource, provider, permission) key because of the unique index, so this dictionary
        // lookup is exact, not a precedence resolution.
        var effectByName = grants.ToDictionary(x => x.Name, x => x.Effect, StringComparer.Ordinal);
        var granted = new HashSet<string>(
            effectByName.Where(x => x.Value == PermissionGrantEffect.Allow).Select(x => x.Key),
            StringComparer.Ordinal);
        var groups = BuildGroups(granted, effectByName);

        return new GetResourcePermissionListResultDto
        {
            ResourceName = resourceName,
            ResourceKey = resourceKey,
            Groups = groups
        };
    }


    [Audited]
    public async Task UpdateAsync(string resourceName, string resourceKey, string providerName, string providerKey, UpdateResourcePermissionsDto input, CancellationToken cancellationToken = default)
    {
        await _authorization.EnsureAuthorizedAsync(PermissionsPolicies.ManagePermissionGrants, cancellationToken);
        var tenantId = _currentTenant.Id;
        resourceName = resourceName.Trim();
        resourceKey = resourceKey.Trim();
        providerName = PermissionProviderNameNormalizer.Normalize(providerName);
        providerKey = PermissionProviderKeyNormalizer.Normalize(providerName, providerKey);

        var updates = input.Permissions
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .GroupBy(x => x.Name.Trim(), StringComparer.Ordinal)
            .Select(g => g.Last())
            .ToArray();

        if (updates.Length == 0)
        {
            return;
        }

        var queryable = await _repository.GetQueryableAsync();
        var existing = await queryable
            .Where(x => x.TenantId == tenantId &&
                        x.ResourceName == resourceName &&
                        x.ResourceKey == resourceKey &&
                        x.ProviderName == providerName &&
                        x.ProviderKey == providerKey)
            .ToListAsync(cancellationToken);

        var map = existing.ToDictionary(x => x.Name, x => x, StringComparer.Ordinal);
        var hasChanges = false;

        foreach (var p in updates)
        {
            var name = p.Name.Trim();

            if (!_definitions.TryGet(name, out var definition))
            {
                throw new ArgumentException($"Unknown permission: {name}", nameof(input));
            }

            EnsureProviderAllowed(definition, providerName);
            EnsureDefinitionGrantable(definition, name);

            if (!p.IsGranted)
            {
                if (map.TryGetValue(name, out var entity))
                {
                    await _repository.DeleteAsync(entity, autoSave: false, cancellationToken: cancellationToken);
                    hasChanges = true;
                }
                continue;
            }

            if (map.TryGetValue(name, out var existingGrant))
            {
                if (existingGrant.Effect != p.Effect)
                {
                    existingGrant.Update(
                        existingGrant.OwnerRevision,
                        p.Effect,
                        existingGrant.ExpiresAtUtc,
                        existingGrant.Reason,
                        existingGrant.TicketId,
                        existingGrant.GrantedByUserId);
                    await _repository.UpdateAsync(existingGrant, autoSave: false, cancellationToken: cancellationToken);
                    hasChanges = true;
                }

                continue;
            }

            await _repository.InsertAsync(
                new ResourcePermissionGrant(
                    Guid.CreateVersion7(), tenantId, name, resourceName, resourceKey, 1,
                    providerName, providerKey, p.Effect, DateTime.UtcNow,
                    expiresAtUtc: null, reason: null, ticketId: null, grantedByUserId: null),
                autoSave: false,
                cancellationToken: cancellationToken);
            hasChanges = true;
        }

        if (hasChanges)
        {
            await _stampService.BumpAsync(tenantId, providerName, providerKey, cancellationToken: cancellationToken);
        }
    }

    private List<PermissionGroupDto> BuildGroups(HashSet<string> granted, Dictionary<string, PermissionGrantEffect> effectByName)
    {
        var result = new List<PermissionGroupDto>();
        foreach (var group in _definitionManager.GetGroups())
        {
            var dto = new PermissionGroupDto
            {
                Name = group.Name,
                DisplayName = group.DisplayName ?? group.Name
            };

            foreach (var p in Enumerate(group.Permissions))
            {
                dto.Permissions.Add(new PermissionGrantInfoDto
                {
                    Name = p.Name,
                    DisplayName = p.DisplayName,
                    ParentName = p.ParentName,
                    IsGranted = granted.Contains(p.Name),
                    Effect = effectByName.TryGetValue(p.Name, out var effect) ? effect : null,
                    AllowedProviders = p.Providers.Count == 0
                        ? DefaultAllowedProviders.ToList()
                        : p.Providers.Select(PermissionProviderNameNormalizer.Normalize).Distinct(StringComparer.Ordinal).ToList()
                });
            }

            result.Add(dto);
        }

        return result;
    }

    private static IEnumerable<Permissions.Application.Contracts.Definitions.PermissionDefinition> Enumerate(IEnumerable<Permissions.Application.Contracts.Definitions.PermissionDefinition> permissions)
    {
        foreach (var p in permissions)
        {
            yield return p;
            foreach (var child in Enumerate(p.Children))
            {
                yield return child;
            }
        }
    }

    private static void EnsureProviderAllowed(Permissions.Application.Contracts.Definitions.PermissionDefinition definition, string providerName)
    {
        var allowedProviders = definition.Providers.Count == 0
            ? DefaultAllowedProviders
            : definition.Providers.Select(PermissionProviderNameNormalizer.Normalize).Distinct(StringComparer.Ordinal).ToArray();

        if (!allowedProviders.Contains(providerName, StringComparer.Ordinal))
        {
            throw new ArgumentException($"Provider '{providerName}' is not allowed for permission '{definition.Name}'.");
        }
    }

    private static void EnsureDefinitionGrantable(Permissions.Application.Contracts.Definitions.PermissionDefinition definition, string permissionName)
    {
        if (!definition.IsEnabled)
        {
            throw new ArgumentException($"Permission '{definition.Name}' is disabled.");
        }

        if (PermissionPatternHelper.IsWildcard(permissionName) && !definition.AllowsWildcard)
        {
            throw new ArgumentException($"Permission '{definition.Name}' does not allow wildcard grants.");
        }
    }
}
