using Permissions.Application.Contracts.Definitions;
using Permissions.Application.Definitions;
using Permissions.Application.Seeding;
using Shared.Permissions.Definitions.AuditLogs;
using Shared.Permissions.Definitions.Auth;
using Shared.Permissions.Definitions.Identity;
using Shared.Permissions.Definitions.Notifications;
using Shared.Permissions.Definitions.Permissions;
using Shared.Permissions.Definitions.Tenant;
using Xunit;

namespace Permissions.Tests;

public class TenantAdminPermissionDefaultsTests
{
    [Fact]
    public void Defaults_should_use_canonical_tenant_admin_role_name()
    {
        Assert.Equal("TenantAdmin", TenantAdminPermissionDefaults.RoleName);
    }

    [Fact]
    public void Every_default_is_defined_by_a_retained_catalog_provider()
    {
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
            TenantAdminPermissionDefaults.ManagementPermissionRoots,
            permissionName => Assert.True(index.IsDefined(permissionName), permissionName));
    }

    [Fact]
    public void Defaults_should_include_tenant_launch_read_and_tenant_access_management()
    {
        var defaults = TenantAdminPermissionDefaults.ManagementPermissionRoots;

        Assert.Contains(TenantPermissions.Launch.Default, defaults);
        Assert.Contains(TenantPermissions.Launch.Read, defaults);
        Assert.Contains(PermissionsPermissions.TenantAccess.Default, defaults);
        Assert.Contains(PermissionsPermissions.TenantAccess.Manage, defaults);
        Assert.DoesNotContain(TenantPermissions.Management.Default, defaults);
        Assert.DoesNotContain(TenantPermissions.Management.Create, defaults);
        Assert.DoesNotContain(PermissionsPermissions.Management.Default, defaults);
        Assert.DoesNotContain(PermissionsPermissions.Management.Grants, defaults);
    }

    [Fact]
    public void Defaults_should_include_v2_tenant_admin_management_permissions()
    {
        Assert.Contains(IdentityPermissions.Users.Default, TenantAdminPermissionDefaults.ManagementPermissionRoots);
        Assert.Contains(IdentityPermissions.Roles.Default, TenantAdminPermissionDefaults.ManagementPermissionRoots);
        Assert.DoesNotContain(IdentityPermissions.Roles.Create, TenantAdminPermissionDefaults.ManagementPermissionRoots);
        Assert.DoesNotContain(IdentityPermissions.Roles.Update, TenantAdminPermissionDefaults.ManagementPermissionRoots);
        Assert.DoesNotContain(IdentityPermissions.Roles.Delete, TenantAdminPermissionDefaults.ManagementPermissionRoots);
        Assert.Contains(IdentityPermissions.Users.Create, TenantAdminPermissionDefaults.ManagementPermissionRoots);
        Assert.Contains(IdentityPermissions.Users.Update, TenantAdminPermissionDefaults.ManagementPermissionRoots);
        Assert.Contains(IdentityPermissions.Users.Delete, TenantAdminPermissionDefaults.ManagementPermissionRoots);
        Assert.Contains(IdentityPermissions.Users.BulkUpdate, TenantAdminPermissionDefaults.ManagementPermissionRoots);
        Assert.Contains(IdentityPermissions.Users.Export, TenantAdminPermissionDefaults.ManagementPermissionRoots);
        Assert.Contains(IdentityPermissions.Users.Import, TenantAdminPermissionDefaults.ManagementPermissionRoots);
        Assert.Contains(IdentityPermissions.SecurityLogs.Default, TenantAdminPermissionDefaults.ManagementPermissionRoots);
        Assert.Contains(IdentityPermissions.Sessions.Default, TenantAdminPermissionDefaults.ManagementPermissionRoots);
        Assert.Contains(IdentityPermissions.Sessions.Revoke, TenantAdminPermissionDefaults.ManagementPermissionRoots);
        Assert.Contains(IdentityPermissions.Sessions.RevokeBulk, TenantAdminPermissionDefaults.ManagementPermissionRoots);
        Assert.DoesNotContain(IdentityPermissions.Sessions.RevokeTenant, TenantAdminPermissionDefaults.ManagementPermissionRoots);
        Assert.Contains(IdentityPermissions.Dashboard.Default, TenantAdminPermissionDefaults.ManagementPermissionRoots);
        Assert.Contains(IdentityPermissions.Dashboard.Tenant, TenantAdminPermissionDefaults.ManagementPermissionRoots);
        Assert.DoesNotContain(IdentityPermissions.Dashboard.Admin, TenantAdminPermissionDefaults.ManagementPermissionRoots);
        Assert.Contains(IdentityPermissions.SecurityAnalytics.Default, TenantAdminPermissionDefaults.ManagementPermissionRoots);
        Assert.Contains(IdentityPermissions.SecurityAnalytics.Tenant, TenantAdminPermissionDefaults.ManagementPermissionRoots);
        Assert.DoesNotContain(IdentityPermissions.SecurityAnalytics.Admin, TenantAdminPermissionDefaults.ManagementPermissionRoots);
        Assert.DoesNotContain(IdentityPermissions.SecuritySettings.Default, TenantAdminPermissionDefaults.ManagementPermissionRoots);
        Assert.DoesNotContain(IdentityPermissions.SecuritySettings.Update, TenantAdminPermissionDefaults.ManagementPermissionRoots);
        Assert.DoesNotContain(AuditLogsPermissions.Read, TenantAdminPermissionDefaults.ManagementPermissionRoots);
        Assert.DoesNotContain(AuditLogsPermissions.ReadDetails, TenantAdminPermissionDefaults.ManagementPermissionRoots);
        Assert.Contains(AuditLogsPermissions.Read, TenantAdminPermissionDefaults.RetiredPermissionNames);
        Assert.Contains(AuditLogsPermissions.ReadDetails, TenantAdminPermissionDefaults.RetiredPermissionNames);
        Assert.Contains(TenantPermissions.Profile.Read, TenantAdminPermissionDefaults.ManagementPermissionRoots);
        Assert.Contains(TenantPermissions.Dashboard.Default, TenantAdminPermissionDefaults.ManagementPermissionRoots);
        Assert.Contains(TenantPermissions.Dashboard.Tenant, TenantAdminPermissionDefaults.ManagementPermissionRoots);
        Assert.DoesNotContain(TenantPermissions.Dashboard.Admin, TenantAdminPermissionDefaults.ManagementPermissionRoots);
        Assert.Contains(NotificationsPermissions.Query.Read, TenantAdminPermissionDefaults.ManagementPermissionRoots);
        Assert.Contains(NotificationsPermissions.Config.Email, TenantAdminPermissionDefaults.ManagementPermissionRoots);
        Assert.Contains(NotificationsPermissions.Config.Sms, TenantAdminPermissionDefaults.ManagementPermissionRoots);
        Assert.Contains(NotificationsPermissions.Config.Push, TenantAdminPermissionDefaults.ManagementPermissionRoots);
        Assert.Contains(NotificationsPermissions.Config.WhatsApp, TenantAdminPermissionDefaults.ManagementPermissionRoots);
        Assert.Contains(NotificationsPermissions.Config.TestSend, TenantAdminPermissionDefaults.ManagementPermissionRoots);
        Assert.Contains(NotificationsPermissions.Templates.Read, TenantAdminPermissionDefaults.ManagementPermissionRoots);
        Assert.Contains(NotificationsPermissions.Templates.Preview, TenantAdminPermissionDefaults.ManagementPermissionRoots);
        Assert.Contains(PermissionsPermissions.TenantAccess.Read, TenantAdminPermissionDefaults.ManagementPermissionRoots);
        Assert.DoesNotContain(NotificationsPermissions.Providers.AnalyticsRead, TenantAdminPermissionDefaults.ManagementPermissionRoots);
        Assert.DoesNotContain(NotificationsPermissions.Templates.Manage, TenantAdminPermissionDefaults.ManagementPermissionRoots);
        Assert.DoesNotContain(NotificationsPermissions.Deliveries.OverrideResend, TenantAdminPermissionDefaults.ManagementPermissionRoots);
    }
}
