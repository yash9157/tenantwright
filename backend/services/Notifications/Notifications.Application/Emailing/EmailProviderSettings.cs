namespace Notifications.Application.Emailing;

/// <summary>Non-secret SMTP settings stored as SettingsJson on a Channel=Email NotificationProviderConfig row.</summary>
internal sealed class EmailProviderSettings
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public string? UserName { get; set; }
    public string FromAddress { get; set; } = "";
    public string? FromName { get; set; }
    public string SecureSocketOption { get; set; } = "StartTls";
}
