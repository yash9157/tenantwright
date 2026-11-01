using Permissions.Application.Contracts.Definitions;

namespace Shared.Permissions.Definitions.Auth;

public sealed class AuthPermissionDefinitionProvider : PermissionDefinitionProvider
{
    public override void Define(IPermissionDefinitionContext context)
    {
        var group = context.AddGroup(AuthPermissions.GroupName, "Authorization Server");
        var management = group.AddPermission(
            AuthPermissions.OpenIddictManagement.Default,
            "Manage OpenIddict");

        var applications = management.AddChild(
            AuthPermissions.OpenIddictManagement.Applications.Default,
            "OpenIddict Applications");
        applications.AddChild(AuthPermissions.OpenIddictManagement.Applications.Read, "Read Applications");
        applications.AddChild(AuthPermissions.OpenIddictManagement.Applications.Create, "Create Applications");
        applications.AddChild(AuthPermissions.OpenIddictManagement.Applications.Update, "Update Applications");
        applications.AddChild(AuthPermissions.OpenIddictManagement.Applications.RotateSecret, "Rotate Application Secrets");
        applications.AddChild(AuthPermissions.OpenIddictManagement.Applications.RevokeSecret, "Revoke Application Secrets");
        applications.AddChild(AuthPermissions.OpenIddictManagement.Applications.Delete, "Delete Applications");

        var scopes = management.AddChild(
            AuthPermissions.OpenIddictManagement.Scopes.Default,
            "OpenIddict Scopes");
        scopes.AddChild(AuthPermissions.OpenIddictManagement.Scopes.Read, "Read Scopes");
        scopes.AddChild(AuthPermissions.OpenIddictManagement.Scopes.Create, "Create Scopes");
        scopes.AddChild(AuthPermissions.OpenIddictManagement.Scopes.Update, "Update Scopes");
        scopes.AddChild(AuthPermissions.OpenIddictManagement.Scopes.Delete, "Delete Scopes");

        var authorizations = management.AddChild(
            AuthPermissions.OpenIddictManagement.Authorizations.Default,
            "Issued Authorizations");
        authorizations.AddChild(AuthPermissions.OpenIddictManagement.Authorizations.Read, "Read Issued Authorizations");
        authorizations.AddChild(AuthPermissions.OpenIddictManagement.Authorizations.Revoke, "Revoke Issued Authorizations");

        var tokens = management.AddChild(
            AuthPermissions.OpenIddictManagement.Tokens.Default,
            "Issued Tokens");
        tokens.AddChild(AuthPermissions.OpenIddictManagement.Tokens.Read, "Read Token Metadata");
        tokens.AddChild(AuthPermissions.OpenIddictManagement.Tokens.Revoke, "Revoke Tokens");
    }
}
