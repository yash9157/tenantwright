namespace Shared.Permissions.Definitions.Identity;

public static class IdentityPermissions
{
    public const string GroupName = "Identity";

    public static class Roles
    {
        public const string Default = GroupName + ".Roles";
        public const string Create = Default + ".Create";
        public const string Update = Default + ".Update";
        public const string Delete = Default + ".Delete";
    }

    public static class Users
    {
        public const string Default = GroupName + ".Users";
        public const string Create = Default + ".Create";
        public const string Update = Default + ".Update";
        public const string Delete = Default + ".Delete";
        public const string BulkUpdate = Default + ".BulkUpdate";
        public const string Export = Default + ".Export";
        public const string Import = Default + ".Import";
        public const string ManagePermissions = Default + ".ManagePermissions";
        public const string ManageRoles = Default + ".ManageRoles";
        public const string Activate = Default + ".Activate";
        public const string Deactivate = Default + ".Deactivate";
        public const string Lock = Default + ".Lock";
        public const string Unlock = Default + ".Unlock";
        public const string Suspend = Default + ".Suspend";
        public const string Restore = Default + ".Restore";
        public const string Terminate = Default + ".Terminate";
        public const string Rehire = Default + ".Rehire";
        public const string Archive = Default + ".Archive";
        public const string LifecycleHistory = Default + ".LifecycleHistory";
        public const string HandoverPrimaryAdministrator = Default + ".HandoverPrimaryAdministrator";

        /// <summary>
        /// Narrow, purpose-limited directory lookup returning identifier, display name, and email
        /// only. Granted to staff who must pick a colleague — a credentialing reviewer, for
        /// example — without receiving <see cref="Default"/> and with it the user-management
        /// console, bulk lifecycle actions, session revocation, and exports.
        /// Its endpoint lives on its own controller: an action added to the users controller would
        /// also inherit that controller's <see cref="Default"/> policy, because ASP.NET Core
        /// accumulates controller and action authorization rather than replacing it.
        /// </summary>
        public const string Search = Default + ".Search";
    }

    public static class PlatformStaff
    {
        public const string Default = GroupName + ".PlatformStaff";
        public const string Create = Default + ".Create";
        public const string Update = Default + ".Update";
        public const string Delete = Default + ".Delete";
        public const string ManageRoles = Default + ".ManageRoles";
        public const string Activate = Default + ".Activate";
        public const string Deactivate = Default + ".Deactivate";
        public const string Lock = Default + ".Lock";
        public const string Unlock = Default + ".Unlock";
        public const string Suspend = Default + ".Suspend";
        public const string Restore = Default + ".Restore";
        public const string Terminate = Default + ".Terminate";
        public const string Rehire = Default + ".Rehire";
        public const string Archive = Default + ".Archive";
        public const string LifecycleHistory = Default + ".LifecycleHistory";
    }

    public static class PlatformRoles
    {
        public const string Default = GroupName + ".PlatformRoles";
        public const string Create = Default + ".Create";
        public const string Update = Default + ".Update";
        public const string Delete = Default + ".Delete";
        public const string ManagePermissions = Default + ".ManagePermissions";
    }

    public static class PlatformStaffInvitations
    {
        public const string Default = GroupName + ".PlatformStaffInvitations";
        public const string Create = Default + ".Create";
        public const string Resend = Default + ".Resend";
        public const string Cancel = Default + ".Cancel";
    }

    public static class OrganizationUnits
    {
        public const string Default = GroupName + ".OrganizationUnits";
        public const string Create = Default + ".Create";
        public const string Update = Default + ".Update";
        public const string Delete = Default + ".Delete";
        public const string ManageUsers = Default + ".ManageUsers";
        public const string ManageRoles = Default + ".ManageRoles";
    }

    public static class ClaimTypes
    {
        public const string Default = GroupName + ".ClaimTypes";
        public const string Create = Default + ".Create";
        public const string Update = Default + ".Update";
        public const string Delete = Default + ".Delete";
    }

    public static class SecurityLogs
    {
        public const string Default = GroupName + ".SecurityLogs";
        public const string ReadDetails = Default + ".ReadDetails";
    }

    public static class Sessions
    {
        public const string Default = GroupName + ".Sessions";
        public const string Revoke = Default + ".Revoke";
        public const string RevokeBulk = Default + ".RevokeBulk";
        public const string RevokeTenant = Default + ".RevokeTenant";
    }

    public static class Dashboard
    {
        public const string Default = GroupName + ".Dashboard";
        public const string Admin = Default + ".Admin";
        public const string Tenant = Default + ".Tenant";
    }

    public static class LoginRisk
    {
        public const string Default = GroupName + ".LoginRisk";
        public const string Override = Default + ".Override";
        public const string FalsePositive = Default + ".FalsePositive";
    }

    public static class SecurityAnalytics
    {
        public const string Default = GroupName + ".SecurityAnalytics";
        public const string Admin = Default + ".Admin";
        public const string Tenant = Default + ".Tenant";
    }

    public static class SecuritySettings
    {
        public const string Default = GroupName + ".SecuritySettings";
        public const string Update = Default + ".Update";
    }

    public static class UserDelegation
    {
        public const string Default = GroupName + ".UserDelegation";
        public const string Manage = Default + ".Manage";
    }

}
