namespace Notifications.Application.Emailing;

public sealed class NotificationsEmailOptions
{
    public SmtpOptions Smtp { get; set; } = new();

    public sealed class SmtpOptions
    {
        public string Host { get; set; } = "";
        public int Port { get; set; } = 587;
        public string UserName { get; set; } = "";
        public string Password { get; set; } = "";
        public string FromAddress { get; set; } = "";
        public string FromName { get; set; } = "";
        public string SecureSocketOption { get; set; } = "StartTls";
    }
}
