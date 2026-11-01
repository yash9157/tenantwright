using Shared.Permissions.Definitions.Auth;

namespace Auth.Authorization;

internal static class OpenIddictManagementPolicies
{
    private const string Prefix = "Auth.Policy.OpenIddictManagement";

    public const string Default = Prefix;

    public static class Applications
    {
        public const string Read = Prefix + ".Applications.Read";
        public const string Create = Prefix + ".Applications.Create";
        public const string Update = Prefix + ".Applications.Update";
        public const string RotateSecret = Prefix + ".Applications.RotateSecret";
        public const string RevokeSecret = Prefix + ".Applications.RevokeSecret";
        public const string Delete = Prefix + ".Applications.Delete";
    }

    public static class Scopes
    {
        public const string Read = Prefix + ".Scopes.Read";
        public const string Create = Prefix + ".Scopes.Create";
        public const string Update = Prefix + ".Scopes.Update";
        public const string Delete = Prefix + ".Scopes.Delete";
    }

    public static class Authorizations
    {
        public const string Read = Prefix + ".Authorizations.Read";
        public const string Revoke = Prefix + ".Authorizations.Revoke";
    }

    public static class Tokens
    {
        public const string Read = Prefix + ".Tokens.Read";
        public const string Revoke = Prefix + ".Tokens.Revoke";
    }

    public static IReadOnlyList<(string PolicyName, string PermissionName)> Mappings { get; } =
    [
        (Default, AuthPermissions.OpenIddictManagement.Default),
        (Applications.Read, AuthPermissions.OpenIddictManagement.Applications.Read),
        (Applications.Create, AuthPermissions.OpenIddictManagement.Applications.Create),
        (Applications.Update, AuthPermissions.OpenIddictManagement.Applications.Update),
        (Applications.RotateSecret, AuthPermissions.OpenIddictManagement.Applications.RotateSecret),
        (Applications.RevokeSecret, AuthPermissions.OpenIddictManagement.Applications.RevokeSecret),
        (Applications.Delete, AuthPermissions.OpenIddictManagement.Applications.Delete),
        (Scopes.Read, AuthPermissions.OpenIddictManagement.Scopes.Read),
        (Scopes.Create, AuthPermissions.OpenIddictManagement.Scopes.Create),
        (Scopes.Update, AuthPermissions.OpenIddictManagement.Scopes.Update),
        (Scopes.Delete, AuthPermissions.OpenIddictManagement.Scopes.Delete),
        (Authorizations.Read, AuthPermissions.OpenIddictManagement.Authorizations.Read),
        (Authorizations.Revoke, AuthPermissions.OpenIddictManagement.Authorizations.Revoke),
        (Tokens.Read, AuthPermissions.OpenIddictManagement.Tokens.Read),
        (Tokens.Revoke, AuthPermissions.OpenIddictManagement.Tokens.Revoke)
    ];
}
