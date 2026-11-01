namespace Notifications.Domain.Shared.Constants;

public static class NotificationsTemplateNames
{
    public const string Layout = "Layout";

    public static class Identity
    {
        public const string PasswordResetLink = "Identity.PasswordResetLink";
        public const string EmailVerificationLink = "Identity.EmailVerificationLink";
        public const string PublicInvitationLink = "Identity.PublicInvitationLink";
        public const string NewDeviceSignInAlert = "Identity.NewDeviceSignInAlert";
        public const string SecurityRiskBlockedAlert = "Identity.SecurityRiskBlockedAlert";
        public const string AdminSessionRevokedAlert = "Identity.AdminSessionRevokedAlert";
        public const string MfaChangedAlert = "Identity.MfaChangedAlert";
    }

    public static class Tenant
    {
        public const string TenantCreated = "Tenant.TenantCreated";
    }

    public static class Permissions
    {
        public const string GrantApprovalRequested = "Permissions.GrantApprovalRequested";
        public const string GrantApprovalDecided = "Permissions.GrantApprovalDecided";
    }
}
