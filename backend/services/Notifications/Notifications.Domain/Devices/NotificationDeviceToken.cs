using Notifications.Domain.Shared.Enums;
using Shared.Domain;
using Shared.Domain.Entities.Auditing;

namespace Notifications.Domain.Devices;

/// <summary>
/// A registered push device token for one user. Prerequisite for real Push sending: a delivery
/// cannot be attempted against a device that has no active, registered token. Device tokens are
/// sensitive identifiers — never logged, masked in diagnostics, and revoked on unregister.
/// </summary>
public class NotificationDeviceToken : FullAuditedAggregateRoot<Guid>, IMultiTenant
{
    /// <summary>Allows the versioned AES-GCM envelope to grow beyond the raw provider token.</summary>
    public const int MaxProtectedDeviceTokenLength = 2048;

    public Guid? TenantId { get; private set; }

    public string UserId { get; private set; } = default!;

    public string DeviceToken { get; private set; } = default!;

    public string? DeviceTokenHash { get; private set; }

    public NotificationDevicePlatform Platform { get; private set; }

    public string? AppIdentifier { get; private set; }

    public bool IsActive { get; private set; }

    public DateTime LastSeenAtUtc { get; private set; }

    protected NotificationDeviceToken()
    {
    }

    public NotificationDeviceToken(
        Guid id,
        Guid? tenantId,
        string userId,
        string deviceToken,
        string deviceTokenHash,
        NotificationDevicePlatform platform,
        string? appIdentifier,
        DateTime nowUtc)
        : base(id)
    {
        TenantId = tenantId;
        UserId = global::Shared.Check.NotNullOrWhiteSpace(userId, nameof(userId), maxLength: 128);
        DeviceToken = global::Shared.Check.NotNullOrWhiteSpace(deviceToken, nameof(deviceToken), maxLength: MaxProtectedDeviceTokenLength);
        DeviceTokenHash = global::Shared.Check.NotNullOrWhiteSpace(deviceTokenHash, nameof(deviceTokenHash), maxLength: 64);
        Platform = platform;
        AppIdentifier = string.IsNullOrWhiteSpace(appIdentifier) ? null : appIdentifier.Trim();
        IsActive = true;
        LastSeenAtUtc = nowUtc;
    }

    public void Touch(DateTime nowUtc)
    {
        IsActive = true;
        LastSeenAtUtc = nowUtc;
    }

    /// <summary>
    /// Refreshes a registration after the device proves possession of its token. This also lets a
    /// shared physical device move safely to the user who most recently registered it.
    /// </summary>
    public void RefreshRegistration(string userId, NotificationDevicePlatform platform, string? appIdentifier, DateTime nowUtc)
    {
        UserId = global::Shared.Check.NotNullOrWhiteSpace(userId, nameof(userId), maxLength: 128);
        Platform = platform;
        AppIdentifier = string.IsNullOrWhiteSpace(appIdentifier) ? null : appIdentifier.Trim();
        Touch(nowUtc);
    }

    /// <summary>
    /// Upgrades a legacy plaintext registration after its token has been verified by the caller.
    /// </summary>
    public void ProtectToken(string protectedDeviceToken, string deviceTokenHash)
    {
        DeviceToken = global::Shared.Check.NotNullOrWhiteSpace(protectedDeviceToken, nameof(protectedDeviceToken), maxLength: MaxProtectedDeviceTokenLength);
        DeviceTokenHash = global::Shared.Check.NotNullOrWhiteSpace(deviceTokenHash, nameof(deviceTokenHash), maxLength: 64);
    }

    public void Deactivate()
    {
        IsActive = false;
    }
}
