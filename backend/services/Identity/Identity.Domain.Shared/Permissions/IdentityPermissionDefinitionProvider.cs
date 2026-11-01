using Permissions.Application.Contracts.Definitions;

namespace Shared.Permissions.Definitions.Identity;

public class IdentityPermissionDefinitionProvider : PermissionDefinitionProvider
{
    public override void Define(IPermissionDefinitionContext context)
    {
        var identityGroup = context.AddGroup(IdentityPermissions.GroupName, "Identity Management");

        // Roles
        var roles = identityGroup.AddPermission(IdentityPermissions.Roles.Default, "Roles");
        roles.AddChild(IdentityPermissions.Roles.Create, "Create");
        roles.AddChild(IdentityPermissions.Roles.Update, "Update");
        roles.AddChild(IdentityPermissions.Roles.Delete, "Delete");

        // Users
        var users = identityGroup.AddPermission(IdentityPermissions.Users.Default, "Users");
        users.AddChild(IdentityPermissions.Users.Create, "Create");
        users.AddChild(IdentityPermissions.Users.Update, "Update");
        users.AddChild(IdentityPermissions.Users.Delete, "Delete");
        users.AddChild(IdentityPermissions.Users.BulkUpdate, "Bulk Update");
        users.AddChild(IdentityPermissions.Users.Search, "Directory Search");
        users.AddChild(IdentityPermissions.Users.Export, "Export");
        users.AddChild(IdentityPermissions.Users.Import, "Import");
        users.AddChild(IdentityPermissions.Users.ManagePermissions, "Manage Permissions");
        users.AddChild(IdentityPermissions.Users.ManageRoles, "Manage Roles");
        users.AddChild(IdentityPermissions.Users.Activate, "Activate");
        users.AddChild(IdentityPermissions.Users.Deactivate, "Deactivate");
        users.AddChild(IdentityPermissions.Users.Lock, "Lock");
        users.AddChild(IdentityPermissions.Users.Unlock, "Unlock");
        users.AddChild(IdentityPermissions.Users.Suspend, "Suspend");
        users.AddChild(IdentityPermissions.Users.Restore, "Restore");
        users.AddChild(IdentityPermissions.Users.Terminate, "Terminate");
        users.AddChild(IdentityPermissions.Users.Rehire, "Rehire");
        users.AddChild(IdentityPermissions.Users.Archive, "Archive");
        users.AddChild(IdentityPermissions.Users.LifecycleHistory, "Lifecycle History");
        users.AddChild(IdentityPermissions.Users.HandoverPrimaryAdministrator, "Handover Primary Administrator");

        // Platform Staff
        var platformStaff = identityGroup.AddPermission(IdentityPermissions.PlatformStaff.Default, "Platform Staff");
        platformStaff.AddChild(IdentityPermissions.PlatformStaff.Create, "Create");
        platformStaff.AddChild(IdentityPermissions.PlatformStaff.Update, "Update");
        platformStaff.AddChild(IdentityPermissions.PlatformStaff.Delete, "Delete");
        platformStaff.AddChild(IdentityPermissions.PlatformStaff.ManageRoles, "Manage Roles");
        platformStaff.AddChild(IdentityPermissions.PlatformStaff.Activate, "Activate");
        platformStaff.AddChild(IdentityPermissions.PlatformStaff.Deactivate, "Deactivate");
        platformStaff.AddChild(IdentityPermissions.PlatformStaff.Lock, "Lock");
        platformStaff.AddChild(IdentityPermissions.PlatformStaff.Unlock, "Unlock");
        platformStaff.AddChild(IdentityPermissions.PlatformStaff.Suspend, "Suspend");
        platformStaff.AddChild(IdentityPermissions.PlatformStaff.Restore, "Restore");
        platformStaff.AddChild(IdentityPermissions.PlatformStaff.Terminate, "Terminate");
        platformStaff.AddChild(IdentityPermissions.PlatformStaff.Rehire, "Rehire");
        platformStaff.AddChild(IdentityPermissions.PlatformStaff.Archive, "Archive");
        platformStaff.AddChild(IdentityPermissions.PlatformStaff.LifecycleHistory, "Lifecycle History");

        // Platform Roles
        var platformRoles = identityGroup.AddPermission(IdentityPermissions.PlatformRoles.Default, "Platform Roles");
        platformRoles.AddChild(IdentityPermissions.PlatformRoles.Create, "Create");
        platformRoles.AddChild(IdentityPermissions.PlatformRoles.Update, "Update");
        platformRoles.AddChild(IdentityPermissions.PlatformRoles.Delete, "Delete");
        platformRoles.AddChild(IdentityPermissions.PlatformRoles.ManagePermissions, "Manage Permissions");

        var platformStaffInvitations = identityGroup.AddPermission(IdentityPermissions.PlatformStaffInvitations.Default, "Platform Staff Invitations");
        platformStaffInvitations.AddChild(IdentityPermissions.PlatformStaffInvitations.Create, "Create");
        platformStaffInvitations.AddChild(IdentityPermissions.PlatformStaffInvitations.Resend, "Resend");
        platformStaffInvitations.AddChild(IdentityPermissions.PlatformStaffInvitations.Cancel, "Cancel");

        // Organization Units
        var ou = identityGroup.AddPermission(IdentityPermissions.OrganizationUnits.Default, "Organization Units");
        ou.AddChild(IdentityPermissions.OrganizationUnits.Create, "Create");
        ou.AddChild(IdentityPermissions.OrganizationUnits.Update, "Update");
        ou.AddChild(IdentityPermissions.OrganizationUnits.Delete, "Delete");
        ou.AddChild(IdentityPermissions.OrganizationUnits.ManageUsers, "Manage Users");
        ou.AddChild(IdentityPermissions.OrganizationUnits.ManageRoles, "Manage Roles");

        // Claim Types
        var claims = identityGroup.AddPermission(IdentityPermissions.ClaimTypes.Default, "Claim Types");
        claims.AddChild(IdentityPermissions.ClaimTypes.Create, "Create");
        claims.AddChild(IdentityPermissions.ClaimTypes.Update, "Update");
        claims.AddChild(IdentityPermissions.ClaimTypes.Delete, "Delete");

        // Security Logs
        var securityLogs = identityGroup.AddPermission(IdentityPermissions.SecurityLogs.Default, "Security Logs");
        securityLogs.AddChild(IdentityPermissions.SecurityLogs.ReadDetails, "Read Forensic Details");

        // Sessions
        var sessions = identityGroup.AddPermission(IdentityPermissions.Sessions.Default, "Sessions");
        sessions.AddChild(IdentityPermissions.Sessions.Revoke, "Revoke");
        sessions.AddChild(IdentityPermissions.Sessions.RevokeBulk, "Bulk Revoke");
        sessions.AddChild(IdentityPermissions.Sessions.RevokeTenant, "Tenant Revoke");

        // Dashboard
        var dashboard = identityGroup.AddPermission(IdentityPermissions.Dashboard.Default, "Dashboard");
        dashboard.AddChild(IdentityPermissions.Dashboard.Admin, "Admin Dashboard");
        dashboard.AddChild(IdentityPermissions.Dashboard.Tenant, "Tenant Dashboard");

        // Login Risk
        var loginRisk = identityGroup.AddPermission(IdentityPermissions.LoginRisk.Default, "Login Risk");
        loginRisk.AddChild(IdentityPermissions.LoginRisk.Override, "Manage Overrides");
        loginRisk.AddChild(IdentityPermissions.LoginRisk.FalsePositive, "Mark False Positive");

        // Security Analytics
        var securityAnalytics = identityGroup.AddPermission(IdentityPermissions.SecurityAnalytics.Default, "Security Analytics");
        securityAnalytics.AddChild(IdentityPermissions.SecurityAnalytics.Admin, "Admin Analytics");
        securityAnalytics.AddChild(IdentityPermissions.SecurityAnalytics.Tenant, "Tenant Analytics");

        // Security Settings
        var securitySettings = identityGroup.AddPermission(IdentityPermissions.SecuritySettings.Default, "Security Settings");
        securitySettings.AddChild(IdentityPermissions.SecuritySettings.Update, "Update Security Settings");

        // User Delegation
        var delegation = identityGroup.AddPermission(IdentityPermissions.UserDelegation.Default, "User Delegation");
        delegation.AddChild(IdentityPermissions.UserDelegation.Manage, "Manage Delegations");

    }
}
