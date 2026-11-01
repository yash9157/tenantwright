using Notifications.Domain.Shared.Enums;

namespace Notifications.Application.Contracts.Devices;

public class RegisterDeviceTokenDto
{
    public string DeviceToken { get; set; } = "";
    public NotificationDevicePlatform Platform { get; set; }
    public string? AppIdentifier { get; set; }
    /// <summary>Browser PushSubscription endpoint. Required when <see cref="Platform"/> is Web.</summary>
    public string? WebPushEndpoint { get; set; }
    /// <summary>Browser PushSubscription <c>p256dh</c> key. Protected at rest and never returned.</summary>
    public string? WebPushP256dh { get; set; }
    /// <summary>Browser PushSubscription <c>auth</c> key. Protected at rest and never returned.</summary>
    public string? WebPushAuth { get; set; }
}

public class NotificationDeviceTokenDto
{
    public Guid Id { get; set; }
    public string DeviceTokenMask { get; set; } = "";
    public NotificationDevicePlatform Platform { get; set; }
    public string? AppIdentifier { get; set; }
    public bool IsActive { get; set; }
    public DateTime LastSeenAtUtc { get; set; }
}

/// <summary>
/// Self-service push device-token registration for the current user. Prerequisite for real Push
/// sending — a delivery is only attempted against an active, registered device token.
/// </summary>
public interface IDeviceRegistrationAppService
{
    /// <summary>Registers (or reactivates/refreshes) a device token for the current user.</summary>
    Task<NotificationDeviceTokenDto> RegisterAsync(RegisterDeviceTokenDto input);

    /// <summary>Deactivates a device token owned by the current user (e.g. on logout).</summary>
    Task UnregisterAsync(Guid id);

    Task<List<NotificationDeviceTokenDto>> GetMyDevicesAsync();
}
