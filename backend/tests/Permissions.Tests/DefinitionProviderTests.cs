using Permissions.Application.Contracts.Definitions;
using Permissions.Application.Definitions;
using Shared.Permissions.Definitions.AuditLogs;
using Shared.Permissions.Definitions.Auth;
using Shared.Permissions.Definitions.Identity;
using Shared.Permissions.Definitions.Notifications;
using Shared.Permissions.Definitions.Permissions;
using Shared.Permissions.Definitions.Tenant;
using Xunit;

namespace Permissions.Tests;

public class DefinitionProviderTests
{
    [Fact]
    public void AuthPermissions_Should_Define_Exact_Application_And_Scope_Actions()
    {
        var context = new PermissionDefinitionContext();
        new AuthPermissionDefinitionProvider().Define(context);

        var group = context.Groups[AuthPermissions.GroupName];
        var management = group.Permissions.Single(x => x.Name == AuthPermissions.OpenIddictManagement.Default);
        var applications = management.Children.Single(x => x.Name == AuthPermissions.OpenIddictManagement.Applications.Default);
        var scopes = management.Children.Single(x => x.Name == AuthPermissions.OpenIddictManagement.Scopes.Default);
        var authorizations = management.Children.Single(x => x.Name == AuthPermissions.OpenIddictManagement.Authorizations.Default);
        var tokens = management.Children.Single(x => x.Name == AuthPermissions.OpenIddictManagement.Tokens.Default);

        Assert.Equal(
            [
                AuthPermissions.OpenIddictManagement.Applications.Read,
                AuthPermissions.OpenIddictManagement.Applications.Create,
                AuthPermissions.OpenIddictManagement.Applications.Update,
                AuthPermissions.OpenIddictManagement.Applications.RotateSecret,
                AuthPermissions.OpenIddictManagement.Applications.RevokeSecret,
                AuthPermissions.OpenIddictManagement.Applications.Delete
            ],
            applications.Children.Select(x => x.Name));
        Assert.Equal(
            [
                AuthPermissions.OpenIddictManagement.Scopes.Read,
                AuthPermissions.OpenIddictManagement.Scopes.Create,
                AuthPermissions.OpenIddictManagement.Scopes.Update,
                AuthPermissions.OpenIddictManagement.Scopes.Delete
            ],
            scopes.Children.Select(x => x.Name));
        Assert.Equal(
            [
                AuthPermissions.OpenIddictManagement.Authorizations.Read,
                AuthPermissions.OpenIddictManagement.Authorizations.Revoke
            ],
            authorizations.Children.Select(x => x.Name));
        Assert.Equal(
            [
                AuthPermissions.OpenIddictManagement.Tokens.Read,
                AuthPermissions.OpenIddictManagement.Tokens.Revoke
            ],
            tokens.Children.Select(x => x.Name));
    }

    [Fact]
    public void PermissionDefinitionManager_Should_Include_Shared_Cross_Service_Dashboard_Permissions()
    {
        var providers = new PermissionDefinitionProvider[]
        {
            new IdentityPermissionDefinitionProvider(),
            new TenantPermissionDefinitionProvider()
        };
        var manager = new PermissionDefinitionManager(providers);
        var index = new PermissionDefinitionIndex(manager);

        Assert.True(index.IsDefined(IdentityPermissions.Dashboard.Default));
        Assert.True(index.IsDefined(IdentityPermissions.Dashboard.Admin));
        Assert.True(index.IsDefined(TenantPermissions.Dashboard.Default));
        Assert.True(index.IsDefined(TenantPermissions.Dashboard.Admin));
    }

    [Fact]
    public void IdentityPermissions_Should_Define_Session_Management_Family()
    {
        var context = new PermissionDefinitionContext();
        new IdentityPermissionDefinitionProvider().Define(context);

        var group = context.Groups[IdentityPermissions.GroupName];
        var sessions = group.Permissions.Single(x => x.Name == IdentityPermissions.Sessions.Default);

        Assert.Contains(sessions.Children, x => x.Name == IdentityPermissions.Sessions.Revoke);
        Assert.Contains(sessions.Children, x => x.Name == IdentityPermissions.Sessions.RevokeBulk);
        Assert.Contains(sessions.Children, x => x.Name == IdentityPermissions.Sessions.RevokeTenant);
    }

    [Fact]
    public void DashboardPermissions_Should_Define_Admin_And_Tenant_Families()
    {
        var identityContext = new PermissionDefinitionContext();
        new IdentityPermissionDefinitionProvider().Define(identityContext);

        var identityGroup = identityContext.Groups[IdentityPermissions.GroupName];
        var identityDashboard = identityGroup.Permissions.Single(x => x.Name == IdentityPermissions.Dashboard.Default);
        Assert.Contains(identityDashboard.Children, x => x.Name == IdentityPermissions.Dashboard.Admin);
        Assert.Contains(identityDashboard.Children, x => x.Name == IdentityPermissions.Dashboard.Tenant);

        var tenantContext = new PermissionDefinitionContext();
        new TenantPermissionDefinitionProvider().Define(tenantContext);

        var tenantGroup = tenantContext.Groups[TenantPermissions.GroupName];
        var tenantDashboard = tenantGroup.Permissions.Single(x => x.Name == TenantPermissions.Dashboard.Default);
        Assert.Contains(tenantDashboard.Children, x => x.Name == TenantPermissions.Dashboard.Admin);
        Assert.Contains(tenantDashboard.Children, x => x.Name == TenantPermissions.Dashboard.Tenant);
    }

    [Fact]
    public void SecurityAnalyticsPermissions_Should_Define_Admin_And_Tenant_Families()
    {
        var context = new PermissionDefinitionContext();
        new IdentityPermissionDefinitionProvider().Define(context);

        var group = context.Groups[IdentityPermissions.GroupName];
        var analytics = group.Permissions.Single(x => x.Name == IdentityPermissions.SecurityAnalytics.Default);

        Assert.Contains(analytics.Children, x => x.Name == IdentityPermissions.SecurityAnalytics.Admin);
        Assert.Contains(analytics.Children, x => x.Name == IdentityPermissions.SecurityAnalytics.Tenant);
    }

    [Fact]
    public void SecuritySettingsPermissions_Should_Define_Update_Family()
    {
        var context = new PermissionDefinitionContext();
        new IdentityPermissionDefinitionProvider().Define(context);

        var group = context.Groups[IdentityPermissions.GroupName];
        var securitySettings = group.Permissions.Single(x => x.Name == IdentityPermissions.SecuritySettings.Default);

        Assert.Contains(securitySettings.Children, x => x.Name == IdentityPermissions.SecuritySettings.Update);
    }

    [Fact]
    public void IdentityUserPermissions_Should_Define_Bulk_And_Transfer_Families()
    {
        var context = new PermissionDefinitionContext();
        new IdentityPermissionDefinitionProvider().Define(context);

        var group = context.Groups[IdentityPermissions.GroupName];
        var users = group.Permissions.Single(x => x.Name == IdentityPermissions.Users.Default);

        Assert.Contains(users.Children, x => x.Name == IdentityPermissions.Users.BulkUpdate);
        Assert.Contains(users.Children, x => x.Name == IdentityPermissions.Users.Export);
        Assert.Contains(users.Children, x => x.Name == IdentityPermissions.Users.Import);
    }

    [Fact]
    public void PermissionsManagement_Should_Define_ImportExport_Family()
    {
        var context = new PermissionDefinitionContext();
        new PermissionsPermissionDefinitionProvider().Define(context);

        var group = context.Groups[PermissionsPermissions.GroupName];
        var management = group.Permissions.Single(x => x.Name == PermissionsPermissions.Management.Default);

        Assert.Contains(management.Children, x => x.Name == PermissionsPermissions.Management.ImportExport.Default);
    }

    [Fact]
    public void PermissionsManagement_Should_Define_Exact_Read_And_Mutation_Families()
    {
        var context = new PermissionDefinitionContext();
        new PermissionsPermissionDefinitionProvider().Define(context);

        var group = context.Groups[PermissionsPermissions.GroupName];
        var management = group.Permissions.Single(x => x.Name == PermissionsPermissions.Management.Default);

        AssertChildren(
            management,
            PermissionsPermissions.Management.Approve.Default,
            PermissionsPermissions.Management.Approve.Read,
            PermissionsPermissions.Management.Approve.Decide,
            PermissionsPermissions.Management.Approve.CancelAny);
        AssertChildren(
            management,
            PermissionsPermissions.Management.Catalog.Default,
            PermissionsPermissions.Management.Catalog.Read,
            PermissionsPermissions.Management.Catalog.Create,
            PermissionsPermissions.Management.Catalog.Update,
            PermissionsPermissions.Management.Catalog.Delete);
        AssertChildren(
            management,
            PermissionsPermissions.Management.ConflictRules.Default,
            PermissionsPermissions.Management.ConflictRules.Read,
            PermissionsPermissions.Management.ConflictRules.Create,
            PermissionsPermissions.Management.ConflictRules.Update,
            PermissionsPermissions.Management.ConflictRules.Delete,
            PermissionsPermissions.Management.ConflictRules.Decide);
        AssertChildren(
            management,
            PermissionsPermissions.Management.Features.Default,
            PermissionsPermissions.Management.Features.Read,
            PermissionsPermissions.Management.Features.Update);
        AssertChildren(
            management,
            PermissionsPermissions.Management.ImportExport.Default,
            PermissionsPermissions.Management.ImportExport.Read,
            PermissionsPermissions.Management.ImportExport.Export,
            PermissionsPermissions.Management.ImportExport.Validate,
            PermissionsPermissions.Management.ImportExport.Apply);
    }

    [Fact]
    public void Notification_provider_configuration_permissions_should_be_feature_neutral()
    {
        var context = new PermissionDefinitionContext();
        new NotificationsPermissionDefinitionProvider().Define(context);

        var root = context.Groups[NotificationsPermissions.GroupName].Permissions.Single(
            permission => permission.Name == NotificationsPermissions.Default);
        var config = root.Children.Single(permission => permission.Name == NotificationsPermissions.Config.Default);

        Assert.All(config.Children, permission => Assert.Empty(permission.RequiredFeatures));
        Assert.DoesNotContain(root.Children, permission => permission.Name == "Notifications.Send");
    }

    private static void AssertChildren(
        PermissionDefinition parent,
        string familyName,
        params string[] expectedChildren)
    {
        var family = parent.Children.Single(x => x.Name == familyName);
        Assert.Equal(expectedChildren, family.Children.Select(x => x.Name));
    }

    [Fact]
    public void AuditLogsPermissions_Should_Define_Grantable_LegalHold_Family()
    {
        var context = new PermissionDefinitionContext();
        new AuditLogsPermissionDefinitionProvider().Define(context);

        var group = context.Groups[AuditLogsPermissions.GroupName];
        var root = group.Permissions.Single(x => x.Name == AuditLogsPermissions.Default);
        var legalHold = root.Children.Single(x => x.Name == AuditLogsPermissions.LegalHold.Default);

        Assert.Contains(legalHold.Children, x => x.Name == AuditLogsPermissions.LegalHold.Read);
        Assert.Contains(legalHold.Children, x => x.Name == AuditLogsPermissions.LegalHold.Create);
        Assert.Contains(legalHold.Children, x => x.Name == AuditLogsPermissions.LegalHold.Release);

        // The controller's legal-hold endpoints require this policy; it must be
        // resolvable/grantable through the definition index.
        var manager = new PermissionDefinitionManager(new PermissionDefinitionProvider[]
        {
            new AuditLogsPermissionDefinitionProvider()
        });
        var index = new PermissionDefinitionIndex(manager);
        Assert.True(index.IsDefined(AuditLogsPermissions.LegalHold.Default));
        Assert.True(index.IsDefined(AuditLogsPermissions.Archive.Read));
        Assert.True(index.IsDefined(AuditLogsPermissions.Archive.Restore));
        Assert.True(index.IsDefined(AuditLogsPermissions.Archive.VerifyIntegrity));
        Assert.True(index.IsDefined(AuditLogsPermissions.SensitivePayload.Read));
        Assert.True(index.IsDefined(AuditLogsPermissions.LegalHold.Read));
        Assert.True(index.IsDefined(AuditLogsPermissions.LegalHold.Create));
        Assert.True(index.IsDefined(AuditLogsPermissions.LegalHold.Release));
    }

    [Fact]
    public void Permissions_Should_Define_Tenant_Access_Read_Family()
    {
        var context = new PermissionDefinitionContext();
        new PermissionsPermissionDefinitionProvider().Define(context);

        var group = context.Groups[PermissionsPermissions.GroupName];
        var tenantAccess = group.Permissions.Single(x => x.Name == PermissionsPermissions.TenantAccess.Default);

        Assert.Contains(tenantAccess.Children, x => x.Name == PermissionsPermissions.TenantAccess.Read);
    }

    [Fact]
    public void Permissions_Should_Define_Exact_Platform_Documentation_Read_Permission()
    {
        var context = new PermissionDefinitionContext();
        new PermissionsPermissionDefinitionProvider().Define(context);

        var group = context.Groups[PermissionsPermissions.GroupName];
        var documentation = group.Permissions.Single(
            x => x.Name == PermissionsPermissions.PlatformDocumentation.Default);

        Assert.Contains(
            documentation.Children,
            x => x.Name == PermissionsPermissions.PlatformDocumentation.Read);
    }
}
