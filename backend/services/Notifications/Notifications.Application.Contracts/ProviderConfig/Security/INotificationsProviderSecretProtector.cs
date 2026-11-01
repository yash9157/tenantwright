using Notifications.Domain.Shared.Enums;

namespace Notifications.Application.Contracts.ProviderConfig.Security;

/// <summary>
/// Encrypts/decrypts provider secrets (API tokens, auth secrets, service-account keys, VAPID
/// private keys) at rest for a NotificationProviderConfig. Service-local to Notifications by
/// design (not Shared.*): only Notifications owns provider-secret business meaning. Lives in
/// Application.Contracts because it is consumed across the Application/Host boundary (e.g. the
/// SMS bootstrap seed contributor in the Notifications host project).
/// </summary>
public interface INotificationsProviderSecretProtector
{
    string Protect(Guid? tenantId, NotificationChannel channel, Guid configId, string plaintext);

    string Unprotect(Guid? tenantId, NotificationChannel channel, Guid configId, string storedValue);

    bool IsProtected(string? storedValue);

    /// <summary>Returns true when an envelope already uses the configured current key/version.</summary>
    bool IsProtectedByCurrentKey(string? storedValue);

    /// <summary>Short, non-reversible fingerprint of a secret for masked display (never the secret itself).</summary>
    string Fingerprint(string plaintext);
}
