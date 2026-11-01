using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Shared.Data.ConnectionStrings;
using Shared.Modularity.DependencyInjection;

namespace Shared.EntityFrameworkCore.ConnectionStrings;

public sealed class TenantConnectionStringProtector : ITenantConnectionStringProtector, ISingletonDependency
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IOptionsMonitor<TenantConnectionStringProtectionOptions> _options;

    public TenantConnectionStringProtector(IOptionsMonitor<TenantConnectionStringProtectionOptions> options)
    {
        _options = options;
    }

    public string Protect(Guid tenantId, string connectionName, string plaintext)
    {
        if (string.IsNullOrWhiteSpace(plaintext))
        {
            throw new InvalidOperationException("Tenant connection string cannot be empty.");
        }

        var options = _options.CurrentValue;
        var keyId = NormalizeKeyId(options.CurrentKeyId);
        var key = ResolveKey(options, keyId);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext.Trim());
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag, CreateAssociatedData(tenantId, connectionName));

        return JsonSerializer.Serialize(
            new TenantConnectionStringEnvelope
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

    public string Unprotect(Guid tenantId, string connectionName, string storedValue)
    {
        if (string.IsNullOrWhiteSpace(storedValue))
        {
            throw new InvalidOperationException("Tenant connection string cannot be empty.");
        }

        if (!TryParseEnvelope(storedValue, out var envelope))
        {
            if (_options.CurrentValue.RequireProtectedValues)
            {
                throw new InvalidOperationException("Tenant connection string is not protected.");
            }

            return storedValue;
        }

        var key = ResolveKey(_options.CurrentValue, envelope.KeyId);
        var nonce = Convert.FromBase64String(envelope.Nonce);
        var ciphertext = Convert.FromBase64String(envelope.Ciphertext);
        var tag = Convert.FromBase64String(envelope.Tag);
        var plaintextBytes = new byte[ciphertext.Length];

        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintextBytes, CreateAssociatedData(tenantId, connectionName));
        return Encoding.UTF8.GetString(plaintextBytes);
    }

    public bool IsProtected(string? storedValue)
    {
        return TryParseEnvelope(storedValue, out _);
    }

    private static bool TryParseEnvelope(string? storedValue, out TenantConnectionStringEnvelope envelope)
    {
        envelope = new TenantConnectionStringEnvelope();
        if (string.IsNullOrWhiteSpace(storedValue))
        {
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<TenantConnectionStringEnvelope>(storedValue, JsonOptions);
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

    private static byte[] ResolveKey(TenantConnectionStringProtectionOptions options, string keyId)
    {
        if (!options.Keys.TryGetValue(keyId, out var encodedKey) || string.IsNullOrWhiteSpace(encodedKey))
        {
            throw new InvalidOperationException($"Tenant connection-string protection key '{keyId}' is not configured.");
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(encodedKey.Trim());
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"Tenant connection-string protection key '{keyId}' must be base64.", ex);
        }

        if (key.Length != KeySize)
        {
            throw new InvalidOperationException($"Tenant connection-string protection key '{keyId}' must be 32 bytes.");
        }

        return key;
    }

    private static string NormalizeKeyId(string? keyId)
    {
        if (string.IsNullOrWhiteSpace(keyId))
        {
            throw new InvalidOperationException("TenantConnectionStrings:Protection:CurrentKeyId is required.");
        }

        return keyId.Trim();
    }

    private static byte[] CreateAssociatedData(Guid tenantId, string connectionName)
    {
        return Encoding.UTF8.GetBytes($"{tenantId:D}|{connectionName.Trim()}");
    }

    private sealed class TenantConnectionStringEnvelope
    {
        public int Version { get; set; }

        public string KeyId { get; set; } = string.Empty;

        public int KeyVersion { get; set; }

        public string Nonce { get; set; } = string.Empty;

        public string Ciphertext { get; set; } = string.Empty;

        public string Tag { get; set; } = string.Empty;
    }
}
