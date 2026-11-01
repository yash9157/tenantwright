namespace Shared.Permissions.Definitions.Permissions;

public static class PermissionsPermissions
{
    public const string GroupName = "Permissions";

    public static class Management
    {
        public const string Default = "Permissions.Management";
        public const string Grants = "Permissions.Management.Grants";
        public static class Approve
        {
            public const string Default = "Permissions.Management.Approve";
            public const string Read = Default + ".Read";
            public const string Decide = Default + ".Decide";
            public const string CancelAny = Default + ".CancelAny";
        }
        public const string History = "Permissions.Management.History";
        public static class Catalog
        {
            public const string Default = "Permissions.Management.Catalog";
            public const string Read = Default + ".Read";
            public const string Create = Default + ".Create";
            public const string Update = Default + ".Update";
            public const string Delete = Default + ".Delete";
        }

        public static class ConflictRules
        {
            public const string Default = "Permissions.Management.ConflictRules";
            public const string Read = Default + ".Read";
            public const string Create = Default + ".Create";
            public const string Update = Default + ".Update";
            public const string Delete = Default + ".Delete";
            public const string Decide = Default + ".Decide";
        }

        public static class Features
        {
            public const string Default = "Permissions.Management.Features";
            public const string Read = Default + ".Read";
            public const string Update = Default + ".Update";
        }
        public const string Evaluate = "Permissions.Management.Evaluate";
        public const string Review = "Permissions.Management.Review";
        public static class ImportExport
        {
            public const string Default = "Permissions.Management.ImportExport";
            public const string Read = Default + ".Read";
            public const string Export = Default + ".Export";
            public const string Validate = Default + ".Validate";
            public const string Apply = Default + ".Apply";
        }

        public static class Snapshots
        {
            public const string Default = "Permissions.Management.Snapshots";
            public const string Read = Default + ".Read";
        }
    }

    public static class TenantAccess
    {
        public const string Default = "Permissions.TenantAccess";
        public const string Read = Default + ".Read";
        public const string Manage = Default + ".Manage";
    }

    public static class PlatformDocumentation
    {
        public const string Default = GroupName + ".PlatformDocumentation";
        public const string Read = Default + ".Read";
    }
}
