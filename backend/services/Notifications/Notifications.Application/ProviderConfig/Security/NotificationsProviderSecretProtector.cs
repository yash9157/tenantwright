using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Notifications.Application.Contracts.ProviderConfig.Security;
using Notifications.Domain.Shared.Enums;
using Shared.Modularity.DependencyInjection;

namespace Notifications.Application.ProviderConfig.Security;

/// <summary>
/// AES-GCM envelope protector for Notifications provider secrets, following the same
/// versioned-envelope/key-rotation shape as Shared.EntityFrameworkCore's
/// TenantConnectionStringProtector, but kept service-local per the multi-channel provider
/// adapter design (Notifications-specific secret meaning, not shared infrastructure).
/// </summary>
[DisableConventionalRegistration]
public sealed class NotificationsProviderSecretProtector : INotificationsProviderSecretProtector, ISingletonDependency
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IOptionsMonitor<NotificationsProviderSecretProtectionOptions> _options;

    public NotificationsProviderSecretProtector(IOptionsMonitor<NotificationsProviderSecretProtectionOptions> options)
    {
        _options = options;
    }

    public string Protect(Guid? tenantId, NotificationChannel channel, Guid configId, string plaintext)
    {
        if (string.IsNullOrWhiteSpace(plaintext))
        {
            throw new InvalidOperationException("Notifications provider secret cannot be empty.");
        }

        var options = _options.CurrentValue;
        var keyId = NormalizeKeyId(options.CurrentKeyId);
        var key = ResolveKey(options, keyId);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext.Trim());
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag, CreateAssociatedData(tenantId, channel, configId));

        return JsonSerializer.Serialize(
            new ProviderSecretEnvelope
            {
                Version = 1,
                KeyId = keyId,
                KeyVersion = Math.Max(1, options.CurrentKeyVersion),
                Nonce = Convert.ToBase64String(nonce),
                Ciphertext = Convert.ToBase64String(ciphertext),
                Tag = Convert.ToBase64String(tag)
            },
            JsonOptions);
    }

    public string Unprotect(Guid? tenantId, NotificationChannel channel, Guid configId, string storedValue)
    {
        if (string.IsNullOrWhiteSpace(storedValue))
        {
            throw new InvalidOperationException("Notifications provider secret cannot be empty.");
        }

        if (!TryParseEnvelope(storedValue, out var envelope))
        {
            if (_options.CurrentValue.RequireProtectedValues)
            {
                throw new InvalidOperationException("Notifications provider secret is not protected.");
            }

            return storedValue;
        }

        var key = ResolveKey(_options.CurrentValue, envelope.KeyId);
        var nonce = Convert.FromBase64String(envelope.Nonce);
        var ciphertext = Convert.FromBase64String(envelope.Ciphertext);
        var tag = Convert.FromBase64String(envelope.Tag);
        var plaintextBytes = new byte[ciphertext.Length];

        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintextBytes, CreateAssociatedData(tenantId, channel, configId));
        return Encoding.UTF8.GetString(plaintextBytes);
    }

    public bool IsProtected(string? storedValue)
    {
        return TryParseEnvelope(storedValue, out _);
    }

    public bool IsProtectedByCurrentKey(string? storedValue)
    {
        if (!TryParseEnvelope(storedValue, out var envelope))
        {
            return false;
        }

        var options = _options.CurrentValue;
        return string.Equals(envelope.KeyId, NormalizeKeyId(options.CurrentKeyId), StringComparison.Ordinal) &&
            envelope.KeyVersion == Math.Max(1, options.CurrentKeyVersion);
    }

    public string Fingerprint(string plaintext)
    {
        if (string.IsNullOrWhiteSpace(plaintext))
        {
            return string.Empty;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(plaintext.Trim()));
        return Convert.ToHexString(hash)[..8].ToLowerInvariant();
    }

    private static bool TryParseEnvelope(string? storedValue, out ProviderSecretEnvelope envelope)
    {
        envelope = new ProviderSecretEnvelope();
        if (string.IsNullOrWhiteSpace(storedValue))
        {
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<ProviderSecretEnvelope>(storedValue, JsonOptions);
            if (parsed is not { Version: 1 } ||
                string.IsNullOrWhiteSpace(parsed.KeyId) ||
                string.IsNullOrWhiteSpace(parsed.Nonce) ||
                string.IsNullOrWhiteSpace(parsed.Ciphertext) ||
                string.IsNullOrWhiteSpace(parsed.Tag))
            {
                return false;
            }

            envelope = parsed;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static byte[] ResolveKey(NotificationsProviderSecretProtectionOptions options, string keyId)
    {
        if (!options.Keys.TryGetValue(keyId, out var encodedKey) || string.IsNullOrWhiteSpace(encodedKey))
        {
            throw new InvalidOperationException($"Notifications provider-secret protection key '{keyId}' is not configured.");
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(encodedKey.Trim());
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"Notifications provider-secret protection key '{keyId}' must be base64.", ex);
        }

        if (key.Length != KeySize)
        {
            throw new InvalidOperationException($"Notifications provider-secret protection key '{keyId}' must be 32 bytes.");
        }

        return key;
    }

    private static string NormalizeKeyId(string? keyId)
    {
        if (string.IsNullOrWhiteSpace(keyId))
        {
            throw new InvalidOperationException("Notifications:ProviderSecrets:Protection:CurrentKeyId is required.");
        }

        return keyId.Trim();
    }

    private static byte[] CreateAssociatedData(Guid? tenantId, NotificationChannel channel, Guid configId)
    {
        var tenantPart = tenantId.HasValue ? tenantId.Value.ToString("D") : "host";
        return Encoding.UTF8.GetBytes($"{tenantPart}|{channel}|{configId:D}");
    }

    private sealed class ProviderSecretEnvelope
    {
        public int Version { get; set; }

        public string KeyId { get; set; } = string.Empty;

        public int KeyVersion { get; set; }

        public string Nonce { get; set; } = string.Empty;

        public string Ciphertext { get; set; } = string.Empty;

        public string Tag { get; set; } = string.Empty;
    }
}
