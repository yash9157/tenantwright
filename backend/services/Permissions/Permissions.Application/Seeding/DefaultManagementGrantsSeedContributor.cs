using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Identity.Domain.Shared.ApplicationUsers;
using Permissions.Application.Contracts.Definitions;
using Permissions.Application.Caching;
using Permissions.Application.Definitions;
using Permissions.Application.PermissionManagement;
using Permissions.Domain.Catalog;
using Permissions.Domain.FeatureManagement;
using Permissions.Domain.Features;
using Permissions.Domain.Grants;
using Permissions.Domain.Shared.Constants;
using Permissions.Domain.Shared.Enums;
using Permissions.Application.Contracts.FeatureManagement;
using Permissions.Application.Contracts.Features;
using Permissions.Application.Contracts.PermissionCatalog;
using Permissions.Application.Contracts.PermissionEvaluation;
using Permissions.Application.Contracts.PermissionManagement;
using Permissions.Application.Contracts.PermissionSnapshots;
using Permissions.Application.Contracts.Provisioning;
using Permissions.Application.Contracts.ResourcePermissionManagement;
using Permissions.Application.Contracts.TenantPermissionAccess;
using Permissions.EntityFrameworkCore.EntityFrameworkCore;
using Shared.Data.Filtering;
using Shared.Data.Seeding;
using Shared.Domain;
using Shared.Domain.Repositories;
using Shared.EntityFrameworkCore.TenantDatabases;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;
using Shared.Permissions.Definitions.AuditLogs;
using Shared.Permissions.Definitions.Auth;
using Shared.Permissions.Definitions.Identity;
using Shared.Permissions.Definitions.Notifications;
using Shared.Permissions.Definitions.Permissions;
using Shared.Permissions.Definitions.Tenant;

namespace Permissions.Application.Seeding;

public class DefaultManagementGrantsSeedContributor : IDataSeedContributor, ITransientDependency
{
    private static readonly Guid DevelopmentDefaultTenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    internal static readonly string[] RequiredBootstrapPermissionNames =
    [
        AuthPermissions.OpenIddictManagement.Default,
        AuthPermissions.OpenIddictManagement.Applications.Default,
        AuthPermissions.OpenIddictManagement.Applications.Read,
        AuthPermissions.OpenIddictManagement.Applications.Create,
        AuthPermissions.OpenIddictManagement.Applications.Update,
        AuthPermissions.OpenIddictManagement.Applications.RotateSecret,
        AuthPermissions.OpenIddictManagement.Applications.RevokeSecret,
        AuthPermissions.OpenIddictManagement.Applications.Delete,
        AuthPermissions.OpenIddictManagement.Scopes.Default,
        AuthPermissions.OpenIddictManagement.Scopes.Read,
        AuthPermissions.OpenIddictManagement.Scopes.Create,
        AuthPermissions.OpenIddictManagement.Scopes.Update,
        AuthPermissions.OpenIddictManagement.Scopes.Delete,
        AuthPermissions.OpenIddictManagement.Authorizations.Default,
        AuthPermissions.OpenIddictManagement.Authorizations.Read,
        AuthPermissions.OpenIddictManagement.Authorizations.Revoke,
        AuthPermissions.OpenIddictManagement.Tokens.Default,
        AuthPermissions.OpenIddictManagement.Tokens.Read,
        AuthPermissions.OpenIddictManagement.Tokens.Revoke,
        IdentityPermissions.Users.Default,
        IdentityPermissions.Users.Create,
        IdentityPermissions.Users.Update,
        IdentityPermissions.Users.Delete,
        IdentityPermissions.Users.BulkUpdate,
        IdentityPermissions.Users.Export,
        IdentityPermissions.Users.Import,
        IdentityPermissions.Users.ManagePermissions,
        IdentityPermissions.PlatformStaff.Default,
        IdentityPermissions.PlatformStaff.Create,
        IdentityPermissions.PlatformStaff.Update,
        IdentityPermissions.PlatformStaff.Delete,
        IdentityPermissions.PlatformRoles.Default,
        IdentityPermissions.PlatformRoles.Create,
        IdentityPermissions.PlatformRoles.Update,
        IdentityPermissions.PlatformRoles.Delete,
        IdentityPermissions.PlatformRoles.ManagePermissions,
        IdentityPermissions.OrganizationUnits.Default,
        IdentityPermissions.OrganizationUnits.Create,
        IdentityPermissions.OrganizationUnits.Update,
        IdentityPermissions.OrganizationUnits.Delete,
        IdentityPermissions.OrganizationUnits.ManageUsers,
        IdentityPermissions.OrganizationUnits.ManageRoles,
        IdentityPermissions.Sessions.Default,
        IdentityPermissions.Sessions.Revoke,
        IdentityPermissions.Sessions.RevokeBulk,
        IdentityPermissions.Sessions.RevokeTenant,
        IdentityPermissions.Dashboard.Default,
        IdentityPermissions.Dashboard.Admin,
        IdentityPermissions.Dashboard.Tenant,
        IdentityPermissions.SecurityLogs.Default,
        IdentityPermissions.LoginRisk.Default,
        IdentityPermissions.LoginRisk.Override,
        IdentityPermissions.LoginRisk.FalsePositive,
        IdentityPermissions.SecurityAnalytics.Default,
        IdentityPermissions.SecurityAnalytics.Admin,
        IdentityPermissions.SecurityAnalytics.Tenant,
        IdentityPermissions.SecuritySettings.Default,
        IdentityPermissions.SecuritySettings.Update,
        IdentityPermissions.UserDelegation.Default,
        IdentityPermissions.UserDelegation.Manage,
        NotificationsPermissions.Inbox.Read,
        NotificationsPermissions.Inbox.MarkRead,
        NotificationsPermissions.Query.Default,
        NotificationsPermissions.Query.Read,
        NotificationsPermissions.Deliveries.Default,
        NotificationsPermissions.Deliveries.Resend,
        NotificationsPermissions.Deliveries.OverrideResend,
        NotificationsPermissions.Config.Default,
        NotificationsPermissions.Config.Email,
        NotificationsPermissions.Config.Sms,
        NotificationsPermissions.Config.Push,
        NotificationsPermissions.Config.WhatsApp,
        NotificationsPermissions.Config.TestSend,
        NotificationsPermissions.Providers.Default,
        NotificationsPermissions.Providers.Read,
        NotificationsPermissions.Providers.ActivityRead,
        NotificationsPermissions.Providers.AnalyticsRead,
        NotificationsPermissions.Presence.Default,
        NotificationsPermissions.Presence.Read,
        NotificationsPermissions.Templates.Default,
        NotificationsPermissions.Templates.Read,
        NotificationsPermissions.Templates.Preview,
        NotificationsPermissions.Templates.Create,
        NotificationsPermissions.Templates.Manage,
        NotificationsPermissions.Templates.Publish,
        NotificationsPermissions.Templates.Rename,
        NotificationsPermissions.Templates.Archive,
        NotificationsPermissions.Templates.DeleteDraft,
        NotificationsPermissions.Replay.Default,
        NotificationsPermissions.Replay.Execute,
        NotificationsPermissions.Replay.OverridePreview,
        NotificationsPermissions.Replay.OverrideExecute,
        PermissionsPermissions.Management.Default,
        PermissionsPermissions.Management.Grants,
        PermissionsPermissions.Management.History,
        PermissionsPermissions.Management.Approve.Read,
        PermissionsPermissions.Management.Approve.Decide,
        PermissionsPermissions.Management.Approve.CancelAny,
        PermissionsPermissions.Management.Catalog.Read,
        PermissionsPermissions.Management.Catalog.Create,
        PermissionsPermissions.Management.Catalog.Update,
        PermissionsPermissions.Management.Catalog.Delete,
        PermissionsPermissions.Management.ConflictRules.Read,
        PermissionsPermissions.Management.ConflictRules.Create,
        PermissionsPermissions.Management.ConflictRules.Update,
        PermissionsPermissions.Management.ConflictRules.Delete,
        PermissionsPermissions.Management.Features.Read,
        PermissionsPermissions.Management.Features.Update,
        PermissionsPermissions.Management.Evaluate,
        PermissionsPermissions.Management.ImportExport.Read,
        PermissionsPermissions.Management.ImportExport.Export,
        PermissionsPermissions.Management.ImportExport.Validate,
        PermissionsPermissions.Management.ImportExport.Apply,
        PermissionsPermissions.Management.Snapshots.Read,
        PermissionsPermissions.TenantAccess.Default,
        PermissionsPermissions.TenantAccess.Read,
        PermissionsPermissions.TenantAccess.Manage,
        PermissionsPermissions.PlatformDocumentation.Default,
        PermissionsPermissions.PlatformDocumentation.Read,
        AuditLogsPermissions.Default,
        AuditLogsPermissions.Read,
        AuditLogsPermissions.ReadDetails,
        AuditLogsPermissions.EntityChanges.Default,
        AuditLogsPermissions.EntityChanges.Read,
        AuditLogsPermissions.Export,
        AuditLogsPermissions.Archive.Default,
        AuditLogsPermissions.Archive.Read,
        AuditLogsPermissions.Archive.Restore,
        AuditLogsPermissions.Archive.VerifyIntegrity,
        AuditLogsPermissions.SensitivePayload.Default,
        AuditLogsPermissions.SensitivePayload.Read,
        AuditLogsPermissions.LegalHold.Default,
        AuditLogsPermissions.LegalHold.Read,
        AuditLogsPermissions.LegalHold.Create,
        AuditLogsPermissions.LegalHold.Release,
        TenantPermissions.Management.Default,
        TenantPermissions.Management.Create,
        TenantPermissions.Management.Update,
        TenantPermissions.Management.Delete,
        TenantPermissions.Management.Archive,
        TenantPermissions.Profile.Default,
        TenantPermissions.Profile.Read,
        TenantPermissions.Dashboard.Default,
        TenantPermissions.Dashboard.Admin,
        TenantPermissions.Dashboard.Tenant
    ];

    private readonly PermissionDefinitionManager _permissionDefinitions;
    private readonly IRepository<PermissionGrant, Guid> _grantRepository;
    private readonly IRepository<PermissionNode, Guid> _catalogRepository;
    private readonly IRepository<PermissionFeatureBinding, Guid> _featureBindingRepository;
    private readonly IRepository<FeatureValue, Guid> _featureValueRepository;
    private readonly IDataFilter _dataFilter;
    private readonly ICurrentTenant _currentTenant;
    private readonly ITenantStore _tenantStore;
    private readonly PermissionProviderGrantCacheSyncService _grantCacheSync;
    private readonly PermissionStampService _stampService;
    private readonly ITenantAdminGrantProvisioningService _tenantAdminGrantProvisioningService;
    private readonly ILogger<DefaultManagementGrantsSeedContributor> _logger;
    private readonly TenantDatabaseFanOut _tenantDatabases;

    public DefaultManagementGrantsSeedContributor(
        PermissionDefinitionManager permissionDefinitions,
        IRepository<PermissionGrant, Guid> grantRepository,
        IRepository<PermissionNode, Guid> catalogRepository,
        IRepository<PermissionFeatureBinding, Guid> featureBindingRepository,
        IRepository<FeatureValue, Guid> featureValueRepository,
        IDataFilter dataFilter,
        ICurrentTenant currentTenant,
        ITenantStore tenantStore,
        PermissionProviderGrantCacheSyncService grantCacheSync,
        PermissionStampService stampService,
        ITenantAdminGrantProvisioningService tenantAdminGrantProvisioningService,
        ILogger<DefaultManagementGrantsSeedContributor> logger,
        TenantDatabaseFanOut tenantDatabases)
    {
        _permissionDefinitions = permissionDefinitions;
        _grantRepository = grantRepository;
        _catalogRepository = catalogRepository;
        _featureBindingRepository = featureBindingRepository;
        _featureValueRepository = featureValueRepository;
        _dataFilter = dataFilter;
        _currentTenant = currentTenant;
        _tenantStore = tenantStore;
        _grantCacheSync = grantCacheSync;
        _stampService = stampService;
        _tenantAdminGrantProvisioningService = tenantAdminGrantProvisioningService;
        _logger = logger;
        _tenantDatabases = tenantDatabases;
    }

    public async Task SeedAsync(DataSeedContext context)
    {
        var managementPermissions = ResolveBootstrapPermissions();
        if (managementPermissions.Count == 0)
        {
            return;
        }

        var tenantIds = await DiscoverTenantIdsAsync();
        if (context.TenantId.HasValue)
        {
            tenantIds.Add(context.TenantId.Value);
        }
        if (IsDevelopment())
        {
            tenantIds.Add(DevelopmentDefaultTenantId);
        }

        await EnsureHostAdminRoleGrantsAsync(managementPermissions);

        foreach (var tenantId in tenantIds)
        {
            if (!await CanSeedTenantAsync(tenantId))
            {
                continue;
            }

            await _tenantAdminGrantProvisioningService.UpsertTenantAdminGrantsAsync(tenantId);
        }
    }

    private static bool IsDevelopment()
    {
        var environmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        return string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase);
    }

    private HashSet<string> ResolveBootstrapPermissions()
    {
        var allDefinedPermissions = Flatten(_permissionDefinitions.GetGroups().SelectMany(x => x.Permissions))
            .Select(x => x.Name)
            .ToHashSet(StringComparer.Ordinal);

        return RequiredBootstrapPermissionNames
            .Where(allDefinedPermissions.Contains)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Every tenant that owns Permissions data, in the shared database and in every dedicated tenant database (where
    /// such a tenant's rows live). Seeding is startup/maintenance work, so an unreachable dedicated database is logged
    /// by the fan-out and retried by its migrator rather than failing the whole seed; each upsert then runs in the
    /// tenant's scope and so writes to the tenant's own database.
    /// </summary>
    private async Task<HashSet<Guid>> DiscoverTenantIdsAsync()
    {
        var perDatabase = await _tenantDatabases.ForEachDatabaseAsync<PermissionsDbContextApp, HashSet<Guid>>(
            "permissions-default-grants-tenant-discovery",
            (_, _) => DiscoverDatabaseTenantIdsAsync());
        return perDatabase.SelectMany(tenantIds => tenantIds).ToHashSet();
    }

    private async Task<HashSet<Guid>> DiscoverDatabaseTenantIdsAsync()
    {
        var tenantIds = new HashSet<Guid>();

        using (_dataFilter.Disable<IMultiTenant>())
        {
            var grantQueryable = (await _grantRepository.GetQueryableAsync()).WhereOwnedByCurrentDatabase();
            var grantTenantIds = await grantQueryable
                .Where(x => x.TenantId.HasValue)
                .Select(x => x.TenantId!.Value)
                .Distinct()
                .ToListAsync();
            tenantIds.UnionWith(grantTenantIds);

            var catalogQueryable = await _catalogRepository.GetQueryableAsync();
            var catalogTenantIds = await catalogQueryable
                .Where(x => x.TenantId.HasValue)
                .Select(x => x.TenantId!.Value)
                .Distinct()
                .ToListAsync();
            tenantIds.UnionWith(catalogTenantIds);

            var bindingQueryable = await _featureBindingRepository.GetQueryableAsync();
            var bindingTenantIds = await bindingQueryable
                .Where(x => x.TenantId.HasValue)
                .Select(x => x.TenantId!.Value)
                .Distinct()
                .ToListAsync();
            tenantIds.UnionWith(bindingTenantIds);

            var featureQueryable = await _featureValueRepository.GetQueryableAsync();
            var featureTenantKeys = await featureQueryable
                .Where(x => x.ProviderName == "T" && x.ProviderKey != null)
                .Select(x => x.ProviderKey!)
                .Distinct()
                .ToListAsync();

            foreach (var providerKey in featureTenantKeys)
            {
                if (Guid.TryParse(providerKey, out var tenantId))
                {
                    tenantIds.Add(tenantId);
                }
            }
        }

        return tenantIds;
    }

    private async Task<bool> CanSeedTenantAsync(Guid tenantId)
    {
        var tenant = await _tenantStore.FindAsync(tenantId);
        var isDevelopmentDefaultTenant = tenantId == DevelopmentDefaultTenantId;

        if (tenant == null)
        {
            _logger.LogWarning(
                "Skipping tenant admin grant seed because tenant was not found. TenantId={TenantId}, IsDevelopmentDefaultTenant={IsDevelopmentDefaultTenant}.",
                tenantId,
                isDevelopmentDefaultTenant);
            return false;
        }

        if (!tenant.IsActive)
        {
            _logger.LogWarning(
                "Skipping tenant admin grant seed because tenant is inactive. TenantId={TenantId}, TenantName={TenantName}, IsDevelopmentDefaultTenant={IsDevelopmentDefaultTenant}.",
                tenantId,
                tenant.Name,
                isDevelopmentDefaultTenant);
            return false;
        }

        return true;
    }

    internal static bool IsTenantGrantSeedCandidate(TenantConfiguration? tenant)
    {
        return tenant is { IsActive: true };
    }

    private async Task EnsureHostAdminRoleGrantsAsync(HashSet<string> permissionNames)
    {
        var roleKey = IdentitySystemRoleIds.HostAdmin.ToString("D");
        // Host grants (TenantId null) are host data and live only in the shared database.
        using (_currentTenant.Change(null))
        {
            var queryable = await _grantRepository.GetQueryableAsync();
            var existingPermissionNames = await queryable
                .Where(x =>
                    x.TenantId == null &&
                    x.ProviderName == PermissionProviderNames.Role &&
                    x.ProviderKey == roleKey)
                .Select(x => x.Name)
                .ToListAsync();

            var existingSet = existingPermissionNames.ToHashSet(StringComparer.Ordinal);
            var now = DateTime.UtcNow;
            var grantsAdded = false;

            foreach (var permissionName in permissionNames)
            {
                if (existingSet.Contains(permissionName))
                {
                    continue;
                }

                await _grantRepository.InsertAsync(
                    new PermissionGrant(
                        id: Guid.CreateVersion7(),
                        tenantId: null,
                        name: permissionName,
                        providerName: PermissionProviderNames.Role,
                        providerKey: roleKey,
                        effect: PermissionGrantEffect.Allow,
                        creationTimeUtc: now),
                    autoSave: true);
                grantsAdded = true;
            }

            if (grantsAdded)
            {
                var stamp = DateTime.UtcNow.Ticks;
                await _grantCacheSync.SyncAsync(
                    null,
                    PermissionProviderNames.Role,
                    roleKey,
                    stamp);
                await _stampService.BumpAsync(
                    null,
                    PermissionProviderNames.Role,
                    roleKey,
                    stamp);
            }
        }
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
