namespace Identity.Application.Account;

internal static class InteractiveTrustDevicePromptPolicy
{
    public static bool ShouldPromptAfterSuccessfulLogin(
        bool promptForTrustDevice,
        string? deviceFingerprint,
        bool isTrustedDevice)
    {
        return promptForTrustDevice &&
               !string.IsNullOrWhiteSpace(deviceFingerprint) &&
               !isTrustedDevice;
    }
}
