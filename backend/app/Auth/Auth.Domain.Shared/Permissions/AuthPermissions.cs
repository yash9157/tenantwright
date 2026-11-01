namespace Shared.Permissions.Definitions.Auth;

public static class AuthPermissions
{
    public const string GroupName = "Auth";

    public static class OpenIddictManagement
    {
        public const string Default = GroupName + ".OpenIddictManagement";

        public static class Applications
        {
            public const string Default = OpenIddictManagement.Default + ".Applications";
            public const string Read = Default + ".Read";
            public const string Create = Default + ".Create";
            public const string Update = Default + ".Update";
            public const string RotateSecret = Default + ".RotateSecret";
            public const string RevokeSecret = Default + ".RevokeSecret";
            public const string Delete = Default + ".Delete";
        }

        public static class Scopes
        {
            public const string Default = OpenIddictManagement.Default + ".Scopes";
            public const string Read = Default + ".Read";
            public const string Create = Default + ".Create";
            public const string Update = Default + ".Update";
            public const string Delete = Default + ".Delete";
        }

        public static class Authorizations
        {
            public const string Default = OpenIddictManagement.Default + ".Authorizations";
            public const string Read = Default + ".Read";
            public const string Revoke = Default + ".Revoke";
        }

        public static class Tokens
        {
            public const string Default = OpenIddictManagement.Default + ".Tokens";
            public const string Read = Default + ".Read";
            public const string Revoke = Default + ".Revoke";
        }
    }
}
