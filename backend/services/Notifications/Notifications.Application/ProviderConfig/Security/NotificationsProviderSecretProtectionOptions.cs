namespace Notifications.Application.ProviderConfig.Security;

public sealed class NotificationsProviderSecretProtectionOptions
{
    public const string SectionName = "Notifications:ProviderSecrets:Protection";

    public string? CurrentKeyId { get; set; }

    public int CurrentKeyVersion { get; set; } = 1;

    public bool RequireProtectedValues { get; set; }

    public Dictionary<string, string> Keys { get; set; } = new(StringComparer.Ordinal);
}
