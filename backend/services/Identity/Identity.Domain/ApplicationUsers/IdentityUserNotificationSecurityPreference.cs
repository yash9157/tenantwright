using Shared.Domain;

namespace Identity.Domain.ApplicationUsers;

public sealed class IdentityUserNotificationSecurityPreference : IMultiTenant
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public Guid UserId { get; set; }
    public bool EmailEnabled { get; set; } = true;
    public bool NewDeviceSignInAlertEnabled { get; set; } = true;
    public bool RiskBlockedAlertEnabled { get; set; } = true;
    public bool AdminSessionRevokedAlertEnabled { get; set; } = true;
    public bool MfaChangedAlertEnabled { get; set; } = true;
    public DateTime CreationTime { get; set; } = DateTime.UtcNow;
    public DateTime? LastModificationTime { get; set; }
}
