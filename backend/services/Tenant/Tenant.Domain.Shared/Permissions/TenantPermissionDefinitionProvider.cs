using Permissions.Application.Contracts.Definitions;

namespace Shared.Permissions.Definitions.Tenant;

public class TenantPermissionDefinitionProvider : PermissionDefinitionProvider
{
    public override void Define(IPermissionDefinitionContext context)
    {
        var group = context.AddGroup(TenantPermissions.GroupName, "Tenant Management");
        var management = group.AddPermission(TenantPermissions.Management.Default, "Manage Tenants");
        management.AddChild(TenantPermissions.Management.Create, "Create Tenant");
        management.AddChild(TenantPermissions.Management.Update, "Update Tenant");
        management.AddChild(TenantPermissions.Management.Delete, "Delete Tenant");
        management.AddChild(TenantPermissions.Management.Archive, "Archive Tenant");
        var breakGlass = group.AddPermission(TenantPermissions.BreakGlass.Default, "Tenant Break-Glass Operations");
        breakGlass.AddChild(TenantPermissions.BreakGlass.ArchiveOverride, "Override Tenant Archive Closure Gate");
        management.AddChild(TenantPermissions.Management.Restore, "Restore Tenant");
        var connectionStrings = management.AddChild(TenantPermissions.ConnectionStrings.Default, "Tenant Connection Strings");
        connectionStrings.AddChild(TenantPermissions.ConnectionStrings.Read, "Read Tenant Connection String Status");
        connectionStrings.AddChild(TenantPermissions.ConnectionStrings.Update, "Update Tenant Connection String");
        connectionStrings.AddChild(TenantPermissions.ConnectionStrings.Delete, "Delete Tenant Connection String");
        connectionStrings.AddChild(TenantPermissions.ConnectionStrings.Test, "Test Tenant Connection String");
        var compliance = management.AddChild(TenantPermissions.Compliance.Default, "Tenant Legal Compliance");
        compliance.AddChild(TenantPermissions.Compliance.Read, "Read Tenant Legal Compliance");
        compliance.AddChild(TenantPermissions.Compliance.Update, "Update Tenant Legal Compliance");
        var dataGovernance = management.AddChild(TenantPermissions.DataGovernance.Default, "Tenant Data Governance");
        dataGovernance.AddChild(TenantPermissions.DataGovernance.Read, "Read Tenant Data Governance");
        dataGovernance.AddChild(TenantPermissions.DataGovernance.Update, "Update Tenant Data Governance");
        var lifecycle = management.AddChild(TenantPermissions.Lifecycle.Default, "Tenant Lifecycle");
        lifecycle.AddChild(TenantPermissions.Lifecycle.Manage, "Manage Tenant Lifecycle");
        var ownership = management.AddChild(TenantPermissions.Ownership.Default, "Tenant Ownership");
        ownership.AddChild(TenantPermissions.Ownership.Transfer, "Transfer Tenant Ownership");
        var provisioning = management.AddChild(TenantPermissions.Provisioning.Default, "Tenant Provisioning");
        provisioning.AddChild(TenantPermissions.Provisioning.Retry, "Retry Tenant Provisioning");
        var closure = management.AddChild(TenantPermissions.Closure.Default, "Tenant Closure");
        closure.AddChild(TenantPermissions.Closure.Request, "Request Tenant Closure");
        closure.AddChild(TenantPermissions.Closure.Manage, "Manage Tenant Closure");
        var profile = group.AddPermission(TenantPermissions.Profile.Default, "Tenant Profile");
        profile.AddChild(TenantPermissions.Profile.Read, "Read Current Tenant Profile");
        profile.AddChild(TenantPermissions.Profile.Update, "Update Tenant Profile");
        var dashboard = group.AddPermission(TenantPermissions.Dashboard.Default, "Tenant Dashboard");
        dashboard.AddChild(TenantPermissions.Dashboard.Admin, "Admin Tenant Dashboard");
        dashboard.AddChild(TenantPermissions.Dashboard.Tenant, "Current Tenant Dashboard");
        var launch = group.AddPermission(TenantPermissions.Launch.Default, "Tenant Launch Readiness");
        launch.AddChild(TenantPermissions.Launch.Read, "Read Tenant Launch Readiness");
    }
}
