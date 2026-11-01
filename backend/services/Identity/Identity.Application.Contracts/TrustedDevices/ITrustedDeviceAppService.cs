using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Identity.Application.Contracts.TrustedDevices;

public sealed class TrustedDeviceDto
{
    public Guid Id { get; set; }
    public string DeviceKey { get; set; } = string.Empty;
    public string DisplayLabel { get; set; } = "Trusted device";
    public DateTime FirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public bool IsExpired { get; set; }
}

public interface ITrustedDeviceAppService
{
    Task<List<TrustedDeviceDto>> GetMyTrustedDevicesAsync(bool includeExpired = false);
    Task RevokeMyTrustedDeviceAsync(Guid id);
    Task<int> RevokeAllMyTrustedDevicesAsync();
}
