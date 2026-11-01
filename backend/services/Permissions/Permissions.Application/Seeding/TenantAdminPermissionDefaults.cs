using Shared.Permissions.Definitions.AuditLogs;
using Shared.Permissions.Definitions.Identity;
using Shared.Permissions.Definitions.Notifications;
using Shared.Permissions.Definitions.Permissions;
using Shared.Permissions.Definitions.Tenant;

namespace Permissions.Application.Seeding;

internal static class TenantAdminPermissionDefaults
{
    public const string RoleName = "TenantAdmin";

    public static readonly IReadOnlyList<string> ManagementPermissionRoots =
    [
        IdentityPermissions.Roles.Default,
        IdentityPermissions.Users.Default,
        IdentityPermissions.Users.Create,
        IdentityPermissions.Users.Update,
        IdentityPermissions.Users.Delete,
        IdentityPermissions.Users.BulkUpdate,
        IdentityPermissions.Users.Export,
        IdentityPermissions.Users.Import,
        // Redundant for this role, which already holds Users.Default; seeded so every surface that
        // depends on user search works for the tenant administrator without a manual grant.
        IdentityPermissions.Users.Search,
        IdentityPermissions.SecurityLogs.Default,
        IdentityPermissions.Sessions.Default,
        IdentityPermissions.Sessions.Revoke,
        IdentityPermissions.Sessions.RevokeBulk,
        IdentityPermissions.Dashboard.Default,
        IdentityPermissions.Dashboard.Tenant,
        IdentityPermissions.SecurityAnalytics.Default,
        IdentityPermissions.SecurityAnalytics.Tenant,
        NotificationsPermissions.Inbox.Read,
        NotificationsPermissions.Inbox.MarkRead,
        NotificationsPermissions.Query.Default,
        NotificationsPermissions.Query.Read,
        NotificationsPermissions.Config.Default,
        NotificationsPermissions.Config.Email,
        NotificationsPermissions.Config.Sms,
        NotificationsPermissions.Config.Push,
        NotificationsPermissions.Config.WhatsApp,
        NotificationsPermissions.Config.TestSend,
        NotificationsPermissions.Templates.Default,
        NotificationsPermissions.Templates.Read,
        NotificationsPermissions.Templates.Preview,
        PermissionsPermissions.TenantAccess.Default,
        PermissionsPermissions.TenantAccess.Read,
        PermissionsPermissions.TenantAccess.Manage,
        TenantPermissions.Profile.Read,
        TenantPermissions.Launch.Default,
        TenantPermissions.Launch.Read,
        TenantPermissions.Dashboard.Default,
        TenantPermissions.Dashboard.Tenant
    ];

    public static readonly IReadOnlySet<string> RetiredPermissionNames = new HashSet<string>(StringComparer.Ordinal)
    {
        AuditLogsPermissions.Read,
        AuditLogsPermissions.ReadDetails
    };
}
