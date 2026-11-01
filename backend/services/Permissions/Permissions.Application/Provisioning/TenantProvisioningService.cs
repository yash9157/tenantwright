using Microsoft.Extensions.Logging;
using Identity.Domain.Shared.ApplicationUsers;
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
using Shared.Modularity.DependencyInjection;
using Shared.Permissions.Definitions.Identity;
using Shared.Permissions.Definitions.Permissions;
using Shared.Permissions.Definitions.Tenant;
using Shared.MultiTenancy;
using Permissions.Application.Contracts.Definitions;
using Shared.Permissions;
using Permissions.Domain.Shared.Constants;
using Permissions.Domain.Shared.Enums;
using Permissions.Application.PermissionManagement;

namespace Permissions.Application.Provisioning;

public class TenantProvisioningService : ITransientDependency
{
    private readonly IPermissionManagementAppService _permissionAppService;
    private readonly IFeatureManagementAppService _featureAppService;
    private readonly PermissionDefinitionManager _permissionDefinitionManager;
    private readonly ICurrentTenant _currentTenant;
    private readonly ILogger<TenantProvisioningService> _logger;

    public TenantProvisioningService(
        IPermissionManagementAppService permissionAppService,
        IFeatureManagementAppService featureAppService,
        PermissionDefinitionManager permissionDefinitionManager,
        ICurrentTenant currentTenant,
        ILogger<TenantProvisioningService> logger)
    {
        _permissionAppService = permissionAppService;
        _featureAppService = featureAppService;
        _permissionDefinitionManager = permissionDefinitionManager;
        _currentTenant = currentTenant;
        _logger = logger;
    }

    public async Task ProvisionTenantAsync(Guid tenantId, List<string> purchasedFeatures)
    {
        _logger.LogInformation("Provisioning tenant {TenantId} with features: {Features}", tenantId, string.Join(", ", purchasedFeatures));

        // 1. Enable Features for the Tenant
        // We can do this from the Host context.
        var featureUpdateDto = new UpdateFeaturesDto
        {
            Features = purchasedFeatures.Select(f => new FeatureValueDto
            {
                Name = f,
                Value = "true"
            }).ToList()
        };
        await _featureAppService.UpdateAsync("T", tenantId.ToString(), featureUpdateDto);

        // 2. Grant Permissions to Admin Role (Recursive)
        // We must switch to the Tenant context to ensure we are updating the correct 'admin' role.
        using (_currentTenant.Change(tenantId))
        {
            var permissionsToGrant = new HashSet<string>();

            // A. Always grant Core Identity Permissions
            AddRecursively(permissionsToGrant, IdentityPermissions.Users.Default);
            AddRecursively(permissionsToGrant, IdentityPermissions.Roles.Default);
            AddRecursively(permissionsToGrant, PermissionsPermissions.Management.Default);
            AddRecursively(permissionsToGrant, TenantPermissions.Management.Default);

            // B. Grant Permissions based on Features
            // Logic: Scan ALL permissions. If a permission requires a feature that we just bought, grant it.
            var allGroups = _permissionDefinitionManager.GetGroups();
            foreach (var group in allGroups)
            {
                foreach (var permission in group.Permissions)
                {
                    // Check if this root permission matches one of our purchased features
                    // Or if it simply requires one of the purchased features
                    if (IsPermissionAllowedByFeatures(permission, purchasedFeatures))
                    {
                        AddRecursively(permissionsToGrant, permission);
                    }
                }
            }

            var tenantAdminRoleId = IdentitySystemRoleIds.TenantAdmin(tenantId).ToString("D");
            var currentPermissions = await _permissionAppService.GetAsync(
                PermissionProviderNames.Role,
                tenantAdminRoleId);
            var permissionUpdateDto = new UpdatePermissionsDto
            {
                ExpectedProviderVersion = currentPermissions.ProviderVersion,
                Permissions = permissionsToGrant.Select(p => new UpdatePermissionDto
                {
                    Name = p,
                    IsGranted = true,
                    Effect = PermissionGrantEffect.Allow
                }).ToList()
            };

            await _permissionAppService.UpdateAsync(
                PermissionProviderNames.Role,
                tenantAdminRoleId,
                permissionUpdateDto);
            _logger.LogInformation(
                "Granted {Count} permissions to TenantAdmin role for tenant {TenantId}.",
                permissionsToGrant.Count,
                tenantId);
        }

        _logger.LogInformation("Provisioning complete.");
    }

    private void AddRecursively(HashSet<string> grantList, string permissionName)
    {
        // 1. Add the permission itself
        grantList.Add(permissionName);

        // 2. Find definition to get children
        var def = FindPermissionDefinition(permissionName);
        if (def != null)
        {
            foreach (var child in def.Children)
            {
                AddRecursively(grantList, child.Name);
            }
        }
    }

    private static void AddRecursively(HashSet<string> grantList, PermissionDefinition permission)
    {
        grantList.Add(permission.Name);
        foreach (var child in permission.Children)
        {
            AddRecursively(grantList, child);
        }
    }

    private PermissionDefinition? FindPermissionDefinition(string name)
    {
        var groups = _permissionDefinitionManager.GetGroups();
        foreach (var group in groups)
        {
            var found = FindInTree(group.Permissions, name);
            if (found != null) return found;
        }
        return null;
    }

    private static PermissionDefinition? FindInTree(IEnumerable<PermissionDefinition> nodes, string name)
    {
        foreach (var node in nodes)
        {
            if (node.Name == name) return node;
            var childFound = FindInTree(node.Children, name);
            if (childFound != null) return childFound;
        }
        return null;
    }

    private static bool IsPermissionAllowedByFeatures(PermissionDefinition permission, List<string> purchasedFeatures)
    {
        // If the permission has NO requirements, we generally don't auto-grant it
        // unless it's explicitly requested (like Identity above).
        // BUT, for Specialty modules, the Root Permission (e.g. "Cardiology.Menu") usually has a requirement.

        if (permission.RequiredFeatures.Count == 0) return false;

        // Check if ALL required features are in the purchased list
        // (Assuming logic is AND. If logic is OR, this would be .Any())
        // Our system default is usually ALL.
        return permission.RequiredFeatures.All(req => purchasedFeatures.Contains(req));
    }
}
