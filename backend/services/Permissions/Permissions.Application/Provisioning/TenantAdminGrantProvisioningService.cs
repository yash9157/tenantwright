using Microsoft.EntityFrameworkCore;
using Identity.Domain.Shared.ApplicationUsers;
using Permissions.Application.Caching;
using Permissions.Application.Contracts.Definitions;
using Permissions.Application.Contracts.FeatureManagement;
using Permissions.Application.Contracts.Features;
using Permissions.Application.Contracts.PermissionCatalog;
using Permissions.Application.Contracts.PermissionEvaluation;
using Permissions.Application.Contracts.PermissionManagement;
using Permissions.Application.Contracts.PermissionSnapshots;
using Permissions.Application.Contracts.Provisioning;
using Permissions.Application.Contracts.ResourcePermissionManagement;
using Permissions.Application.Contracts.TenantPermissionAccess;
using Permissions.Application.Definitions;
using Permissions.Application.Seeding;
using Permissions.Application.PermissionManagement;
using Permissions.Domain.Grants;
using Permissions.Domain.Shared.Constants;
using Permissions.Domain.Shared.Enums;
using Permissions.EntityFrameworkCore.EntityFrameworkCore;
using Shared.Domain.Repositories;
using Shared.EntityFrameworkCore;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;

namespace Permissions.Application.Provisioning;

[ExposeServices(
    typeof(ITenantAdminGrantProvisioningService),
    IncludeSelf = true)]
public class TenantAdminGrantProvisioningService :
    ITenantAdminGrantProvisioningService,
    ITransientDependency
{
    private readonly PermissionDefinitionManager _permissionDefinitions;
    private readonly IRepository<PermissionGrant, Guid> _grantRepository;
    private readonly PermissionProviderGrantCacheSyncService _grantCacheSync;
    private readonly PermissionStampService _stampService;
    private readonly ICurrentTenant _currentTenant;
    private readonly IDbContextProvider<PermissionsDbContextApp> _dbContextProvider;

    public TenantAdminGrantProvisioningService(
        PermissionDefinitionManager permissionDefinitions,
        IRepository<PermissionGrant, Guid> grantRepository,
        PermissionProviderGrantCacheSyncService grantCacheSync,
        PermissionStampService stampService,
        ICurrentTenant currentTenant,
        IDbContextProvider<PermissionsDbContextApp> dbContextProvider)
    {
        _permissionDefinitions = permissionDefinitions;
        _grantRepository = grantRepository;
        _grantCacheSync = grantCacheSync;
        _stampService = stampService;
        _currentTenant = currentTenant;
        _dbContextProvider = dbContextProvider;
    }

    public async Task UpsertTenantAdminGrantsAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant id is required.", nameof(tenantId));
        }

        var roleKey = IdentitySystemRoleIds.TenantAdmin(tenantId).ToString("D");
        var validPermissions = ResolveValidPermissions();
        if (validPermissions.Count == 0)
        {
            throw new InvalidOperationException("No defined TenantAdmin permission defaults are available for provisioning.");
        }

        using (_currentTenant.Change(tenantId))
        {
            var dbContext = await _dbContextProvider.GetDbContextAsync();
            var hostManagedCeiling = await dbContext.PermissionAssignmentStates.AsNoTracking()
                .AnyAsync(x => x.TenantId == tenantId &&
                               x.ProviderName == PermissionProviderNames.Role &&
                               x.ProviderKey == roleKey,
                    cancellationToken);
            var retiredPermissionNames = TenantAdminPermissionDefaults.RetiredPermissionNames.ToArray();
            var queryable = await _grantRepository.GetQueryableAsync();
            var existing = await queryable
                .Where(x =>
                    x.TenantId == tenantId &&
                    x.ProviderName == PermissionProviderNames.Role &&
                    x.ProviderKey == roleKey &&
                    (validPermissions.Contains(x.Name) ||
                     retiredPermissionNames.Contains(x.Name)))
                .ToListAsync(cancellationToken);

            var retired = existing
                .Where(x => TenantAdminPermissionDefaults.RetiredPermissionNames.Contains(x.Name))
                .ToList();
            if (retired.Count > 0)
            {
                await _grantRepository.DeleteManyAsync(
                    retired,
                    autoSave: false,
                    cancellationToken: cancellationToken);
            }

            var existingMap = existing
                .Where(x => validPermissions.Contains(x.Name))
                .ToDictionary(x => x.Name, StringComparer.Ordinal);
            // Once a Host Admin has saved this tenant's delegation ceiling, the assignment-state row is
            // the durable ownership marker. Provisioning must never restore permissions the operator
            // deliberately removed. Before that first governed save, retain the idempotent bootstrap
            // behavior for new and previously provisioned tenants.
            IEnumerable<string> permissionsToProvision = hostManagedCeiling ? [] : validPermissions;
            foreach (var permission in permissionsToProvision)
            {
                if (existingMap.TryGetValue(permission, out var grant))
                {
                    if (grant.Effect != PermissionGrantEffect.Allow)
                    {
                        grant.SetEffect(PermissionGrantEffect.Allow);
                    }

                    await _grantRepository.UpdateAsync(grant, autoSave: false, cancellationToken: cancellationToken);

                    continue;
                }

                await _grantRepository.InsertAsync(
                    new PermissionGrant(
                        id: Guid.CreateVersion7(),
                        tenantId: tenantId,
                        name: permission,
                        providerName: PermissionProviderNames.Role,
                        providerKey: roleKey,
                        effect: PermissionGrantEffect.Allow,
                        creationTimeUtc: DateTime.UtcNow),
                    autoSave: false,
                    cancellationToken: cancellationToken);
            }

            // Cache synchronization queries the database with AsNoTracking.
            // Flush the idempotent grant mutations inside the ambient transaction
            // before building that projection; the cache write remains UoW-aware.
            await dbContext.SaveChangesAsync(cancellationToken);

            var stamp = DateTime.UtcNow.Ticks;
            await _grantCacheSync.SyncAsync(
                tenantId,
                PermissionProviderNames.Role,
                roleKey,
                stamp,
                cancellationToken: cancellationToken);
            await _stampService.BumpAsync(
                tenantId,
                PermissionProviderNames.Role,
                roleKey,
                stamp,
                cancellationToken: cancellationToken);
        }
    }

    public async Task DeleteTenantAdminGrantsAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant id is required.", nameof(tenantId));
        }

        var roleKey = IdentitySystemRoleIds.TenantAdmin(tenantId).ToString("D");

        using (_currentTenant.Change(tenantId))
        {
            var queryable = await _grantRepository.GetQueryableAsync();
            var grants = await queryable
                .Where(x =>
                    x.TenantId == tenantId &&
                    x.ProviderName == PermissionProviderNames.Role &&
                    x.ProviderKey == roleKey)
                .ToListAsync(cancellationToken);

            if (grants.Count > 0)
            {
                await _grantRepository.DeleteManyAsync(
                    grants,
                    autoSave: true,
                    cancellationToken: cancellationToken);
            }

            var stamp = DateTime.UtcNow.Ticks;
            await _grantCacheSync.SyncAsync(
                tenantId,
                PermissionProviderNames.Role,
                roleKey,
                stamp,
                cancellationToken: cancellationToken);
            await _stampService.BumpAsync(
                tenantId,
                PermissionProviderNames.Role,
                roleKey,
                stamp,
                cancellationToken: cancellationToken);
        }
    }

    private HashSet<string> ResolveValidPermissions()
    {
        var defined = Flatten(_permissionDefinitions.GetGroups().SelectMany(x => x.Permissions))
            .Select(x => x.Name)
            .ToHashSet(StringComparer.Ordinal);

        return TenantAdminPermissionDefaults.ManagementPermissionRoots
            .Where(defined.Contains)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static IEnumerable<PermissionDefinition> Flatten(IEnumerable<PermissionDefinition> permissions)
    {
        foreach (var permission in permissions)
        {
            yield return permission;
            foreach (var child in Flatten(permission.Children))
            {
                yield return child;
            }
        }
    }
}
