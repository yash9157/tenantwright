using Permissions.Application.Contracts.Definitions;
using Permissions.Application.Definitions;
using Permissions.Application.Seeding;
using Shared.MultiTenancy;
using Shared.Permissions.Definitions.AuditLogs;
using Shared.Permissions.Definitions.Auth;
using Shared.Permissions.Definitions.Identity;
using Shared.Permissions.Definitions.Notifications;
using Shared.Permissions.Definitions.Permissions;
using Shared.Permissions.Definitions.Tenant;
using Xunit;

namespace Permissions.Tests;

public class DefaultManagementGrantsSeedContributorTests
{
    [Fact]
    public void Every_bootstrap_permission_is_defined_by_a_retained_catalog_provider()
    {
        // Seeding silently drops undefined names, so a stale bootstrap entry would
        // never be granted. Pin the list to the definition providers that remain.
        var index = new PermissionDefinitionIndex(new PermissionDefinitionManager(
        [
            new AuditLogsPermissionDefinitionProvider(),
            new AuthPermissionDefinitionProvider(),
            new IdentityPermissionDefinitionProvider(),
            new NotificationsPermissionDefinitionProvider(),
            new PermissionsPermissionDefinitionProvider(),
            new TenantPermissionDefinitionProvider()
        ]));

        Assert.All(
            DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames,
            permissionName => Assert.True(index.IsDefined(permissionName), permissionName));
    }

    [Fact]
    public void Tenant_grant_seed_candidate_should_allow_active_shared_database_tenant()
    {
        var tenant = new TenantConfiguration
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Name = "default",
            NormalizedName = "DEFAULT",
            IsActive = true
        };

        Assert.True(DefaultManagementGrantsSeedContributor.IsTenantGrantSeedCandidate(tenant));
    }

    [Fact]
    public void Tenant_grant_seed_candidate_should_reject_inactive_or_missing_tenant()
    {
        Assert.False(DefaultManagementGrantsSeedContributor.IsTenantGrantSeedCandidate(null));

        Assert.False(DefaultManagementGrantsSeedContributor.IsTenantGrantSeedCandidate(new TenantConfiguration
        {
            Id = Guid.NewGuid(),
            Name = "inactive",
            NormalizedName = "INACTIVE",
            IsActive = false
        }));
    }

    [Fact]
    public void Bootstrap_permissions_include_admin_console_runtime_surfaces()
    {
        Assert.Contains(AuthPermissions.OpenIddictManagement.Default, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(IdentityPermissions.OrganizationUnits.Default, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(IdentityPermissions.OrganizationUnits.ManageUsers, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(IdentityPermissions.OrganizationUnits.ManageRoles, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(IdentityPermissions.PlatformStaff.Default, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(IdentityPermissions.PlatformStaff.Create, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(IdentityPermissions.PlatformStaff.Update, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(IdentityPermissions.PlatformStaff.Delete, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(IdentityPermissions.PlatformRoles.Default, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(IdentityPermissions.PlatformRoles.Create, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(IdentityPermissions.PlatformRoles.Update, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(IdentityPermissions.PlatformRoles.Delete, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(IdentityPermissions.PlatformRoles.ManagePermissions, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(IdentityPermissions.Users.ManagePermissions, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(IdentityPermissions.SecurityLogs.Default, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(IdentityPermissions.LoginRisk.Default, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(IdentityPermissions.LoginRisk.Override, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(IdentityPermissions.LoginRisk.FalsePositive, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(IdentityPermissions.UserDelegation.Manage, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(TenantPermissions.Management.Default, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(TenantPermissions.Management.Create, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(TenantPermissions.Management.Update, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(TenantPermissions.Management.Delete, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(TenantPermissions.Management.Archive, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.DoesNotContain(TenantPermissions.BreakGlass.Default, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.DoesNotContain(TenantPermissions.BreakGlass.ArchiveOverride, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(TenantPermissions.Profile.Read, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(TenantPermissions.Dashboard.Admin, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(AuditLogsPermissions.Default, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(AuditLogsPermissions.Read, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(AuditLogsPermissions.ReadDetails, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(AuditLogsPermissions.EntityChanges.Read, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(AuditLogsPermissions.Export, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(AuditLogsPermissions.Archive.Read, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(AuditLogsPermissions.Archive.Restore, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(AuditLogsPermissions.Archive.VerifyIntegrity, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(AuditLogsPermissions.SensitivePayload.Read, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(AuditLogsPermissions.LegalHold.Read, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(AuditLogsPermissions.LegalHold.Create, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(AuditLogsPermissions.LegalHold.Release, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(NotificationsPermissions.Query.Read, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(NotificationsPermissions.Deliveries.Resend, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(NotificationsPermissions.Deliveries.OverrideResend, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(NotificationsPermissions.Config.Email, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(NotificationsPermissions.Config.Sms, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(NotificationsPermissions.Config.Push, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(NotificationsPermissions.Config.WhatsApp, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(NotificationsPermissions.Config.TestSend, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(NotificationsPermissions.Providers.Read, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(NotificationsPermissions.Providers.ActivityRead, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(NotificationsPermissions.Providers.AnalyticsRead, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(NotificationsPermissions.Presence.Read, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(NotificationsPermissions.Templates.Read, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(NotificationsPermissions.Templates.Preview, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(NotificationsPermissions.Templates.Create, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(NotificationsPermissions.Templates.Manage, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(NotificationsPermissions.Templates.Publish, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(NotificationsPermissions.Templates.Rename, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(NotificationsPermissions.Templates.Archive, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(NotificationsPermissions.Templates.DeleteDraft, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(NotificationsPermissions.Replay.Execute, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(NotificationsPermissions.Replay.OverridePreview, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(NotificationsPermissions.Replay.OverrideExecute, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(PermissionsPermissions.TenantAccess.Read, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(PermissionsPermissions.PlatformDocumentation.Read, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
        Assert.Contains(PermissionsPermissions.Management.ImportExport.Apply, DefaultManagementGrantsSeedContributor.RequiredBootstrapPermissionNames);
    }

}
