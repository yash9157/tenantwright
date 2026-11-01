using Shared.AuditLogs.Auditing;
using Shared.Domain;

namespace Identity.Domain.ApplicationUsers;

public class IdentityTrustedDevice : IMultiTenant
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public Guid UserId { get; set; }

    /// <summary>
    /// HMAC-SHA256(user.DeviceSecret, deviceFingerprint) stored as hex.
    /// Binding the hash to a per-user server-side secret means a leaked or
    /// guessed fingerprint cannot be replayed to skip MFA.
    /// </summary>
    public string DeviceHash { get; set; } = default!;

    public DateTime FirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
}
