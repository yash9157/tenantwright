namespace Shared.Permissions.Definitions.Tenant;

public static class TenantPermissions
{
    public const string GroupName = "Tenant";

    public static class Management
    {
        public const string Default = GroupName + ".Management";
        public const string Create = Default + ".Create";
        public const string Update = Default + ".Update";
        public const string Delete = Default + ".Delete";
        public const string Archive = Default + ".Archive";
        public const string Restore = Default + ".Restore";
    }

    public static class BreakGlass
    {
        public const string Default = GroupName + ".BreakGlass";
        public const string ArchiveOverride = Default + ".ArchiveOverride";
    }

    /// <summary>
    /// Fine-grained policies for tenant connection-string management. These are
    /// children of <see cref="Management.Default"/>, so an existing exact grant of
    /// the parent (or a <c>Tenant.Management.*</c> wildcard) still inherits them
    /// (see ValkeyPermissionChecker parent-inheritance). Narrowly-scoped roles that
    /// held only <c>Tenant.Management.Update</c> need an explicit grant migration.
    /// </summary>
    public static class ConnectionStrings
    {
        public const string Default = Management.Default + ".ConnectionStrings";
        public const string Read = Default + ".Read";
        public const string Update = Default + ".Update";
        public const string Delete = Default + ".Delete";
        public const string Test = Default + ".Test";
    }

    public static class Compliance
    {
        public const string Default = Management.Default + ".Compliance";
        public const string Read = Default + ".Read";
        public const string Update = Default + ".Update";
    }

    public static class DataGovernance
    {
        public const string Default = Management.Default + ".DataGovernance";
        public const string Read = Default + ".Read";
        public const string Update = Default + ".Update";
    }

    public static class Profile
    {
        public const string Default = GroupName + ".Profile";
        public const string Read = Default + ".Read";
        public const string Update = Default + ".Update";
    }

    public static class Lifecycle
    {
        public const string Default = GroupName + ".Management.Lifecycle";
        public const string Manage = Default + ".Manage";
    }

    public static class Ownership
    {
        public const string Default = GroupName + ".Management.Ownership";
        public const string Transfer = Default + ".Transfer";
    }

    public static class Provisioning
    {
        public const string Default = Management.Default + ".Provisioning";
        public const string Retry = Default + ".Retry";
    }

    public static class Closure
    {
        public const string Default = GroupName + ".Management.Closure";
        public const string Request = Default + ".Request";
        public const string Manage = Default + ".Manage";
    }

    public static class Dashboard
    {
        public const string Default = GroupName + ".Dashboard";
        public const string Admin = Default + ".Admin";
        public const string Tenant = Default + ".Tenant";
    }

    public static class Launch
    {
        public const string Default = GroupName + ".Launch";
        public const string Read = Default + ".Read";
    }
}
