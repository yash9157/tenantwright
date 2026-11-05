using System.Security.Cryptography;
using System.Text;

namespace Notifications.Domain.Notifications;

public static class NotificationDestinationFingerprint
{
    public static string Compute(string destination) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(destination)));
}
