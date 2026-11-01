namespace Identity.Application.Account;

internal static class InteractiveMfaTrustedDevicePolicy
{
    public static bool ShouldPersistTrustedDevice(
        bool rememberTrustedDevice,
        string? deviceFingerprint)
    {
        return rememberTrustedDevice &&
               !string.IsNullOrWhiteSpace(deviceFingerprint);
    }
}
