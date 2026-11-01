using Identity.Application.Account;
using Xunit;

namespace Identity.Tests;

public class InteractiveMfaTrustedDevicePolicyTests
{
    [Fact]
    public void ShouldPersistTrustedDevice_ReturnsTrue_WhenUserOptsInAndFingerprintExists()
    {
        var shouldPersist = InteractiveMfaTrustedDevicePolicy.ShouldPersistTrustedDevice(
            rememberTrustedDevice: true,
            deviceFingerprint: "device-fingerprint");

        Assert.True(shouldPersist);
    }

    [Fact]
    public void ShouldPersistTrustedDevice_ReturnsFalse_WhenUserDoesNotOptIn()
    {
        var shouldPersist = InteractiveMfaTrustedDevicePolicy.ShouldPersistTrustedDevice(
            rememberTrustedDevice: false,
            deviceFingerprint: "device-fingerprint");

        Assert.False(shouldPersist);
    }

    [Fact]
    public void ShouldPersistTrustedDevice_ReturnsFalse_WhenFingerprintIsMissing()
    {
        var shouldPersist = InteractiveMfaTrustedDevicePolicy.ShouldPersistTrustedDevice(
            rememberTrustedDevice: true,
            deviceFingerprint: null);

        Assert.False(shouldPersist);
    }
}
