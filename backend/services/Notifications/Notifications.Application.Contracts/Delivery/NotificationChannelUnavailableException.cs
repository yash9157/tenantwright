namespace Notifications.Application.Contracts.Delivery;

public sealed class NotificationChannelUnavailableException : InvalidOperationException
{
    public string ErrorCode { get; }

    public NotificationChannelUnavailableException(string errorCode, string message)
        : base(message)
    {
        ErrorCode = string.IsNullOrWhiteSpace(errorCode)
            ? "channel_unavailable"
            : errorCode.Trim();
    }
}
