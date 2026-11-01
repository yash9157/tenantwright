using Identity.Application.Account;
using Xunit;

namespace Identity.Tests;

public class InteractiveTrustDevicePromptPolicyTests
{
    [Fact]
    public void ShouldPromptAfterSuccessfulLogin_ReturnsFalse_WhenPromptingIsDisabled()
    {
        var shouldPrompt = InteractiveTrustDevicePromptPolicy.ShouldPromptAfterSuccessfulLogin(
            promptForTrustDevice: false,
            deviceFingerprint: "device-a",
            isTrustedDevice: false);

        Assert.False(shouldPrompt);
    }

    [Fact]
    public void ShouldPromptAfterSuccessfulLogin_ReturnsFalse_WhenFingerprintIsMissing()
    {
        var shouldPrompt = InteractiveTrustDevicePromptPolicy.ShouldPromptAfterSuccessfulLogin(
            promptForTrustDevice: true,
            deviceFingerprint: null,
            isTrustedDevice: false);

        Assert.False(shouldPrompt);
    }

    [Fact]
    public void ShouldPromptAfterSuccessfulLogin_ReturnsFalse_WhenDeviceIsAlreadyTrusted()
    {
        var shouldPrompt = InteractiveTrustDevicePromptPolicy.ShouldPromptAfterSuccessfulLogin(
            promptForTrustDevice: true,
            deviceFingerprint: "device-a",
            isTrustedDevice: true);

        Assert.False(shouldPrompt);
    }

    [Fact]
    public void ShouldPromptAfterSuccessfulLogin_ReturnsTrue_ForUntrustedFingerprint()
    {
        var shouldPrompt = InteractiveTrustDevicePromptPolicy.ShouldPromptAfterSuccessfulLogin(
            promptForTrustDevice: true,
            deviceFingerprint: "device-a",
            isTrustedDevice: false);

        Assert.True(shouldPrompt);
    }
}
