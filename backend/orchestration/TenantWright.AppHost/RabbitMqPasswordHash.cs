using System.Security.Cryptography;
using System.Text;

namespace TenantWright.AppHost;

/// <summary>
/// RabbitMQ's internal-backend password hash for definition files (<c>rabbit_password_hashing_sha256</c>):
/// <c>base64(salt || SHA-256(salt || utf8(password)))</c> with a random 32-bit salt, as documented at
/// https://www.rabbitmq.com/docs/passwords#computing-password-hash.
/// </summary>
internal static class RabbitMqPasswordHash
{
    public const string HashingAlgorithm = "rabbit_password_hashing_sha256";

    private const int SaltLength = 4;
    private const int HashLength = SaltLength + (SHA256.HashSizeInBits / 8);

    public static string Create(string password)
    {
        Span<byte> salt = stackalloc byte[SaltLength];
        RandomNumberGenerator.Fill(salt);
        return Create(password, salt);
    }

    /// <summary>Computes the hash for a known salt; exposed for the documented known-answer test.</summary>
    public static string Create(string password, ReadOnlySpan<byte> salt)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (salt.Length != SaltLength)
        {
            throw new ArgumentException($"The RabbitMQ password salt must be {SaltLength} bytes.", nameof(salt));
        }

        var passwordBytes = Encoding.UTF8.GetBytes(password);
        var saltedPassword = new byte[SaltLength + passwordBytes.Length];
        var result = new byte[HashLength];
        try
        {
            salt.CopyTo(saltedPassword);
            passwordBytes.CopyTo(saltedPassword, SaltLength);
            salt.CopyTo(result);
            SHA256.HashData(saltedPassword, result.AsSpan(SaltLength));
            return Convert.ToBase64String(result);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
            CryptographicOperations.ZeroMemory(saltedPassword);
        }
    }

    /// <summary>Whether <paramref name="passwordHash"/> is a well-formed SHA-256 hash of <paramref name="password"/>.</summary>
    public static bool Verify(string password, string? passwordHash)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (string.IsNullOrEmpty(passwordHash))
        {
            return false;
        }

        Span<byte> decoded = stackalloc byte[HashLength];
        if (!Convert.TryFromBase64String(passwordHash, decoded, out var written) || written != HashLength)
        {
            return false;
        }

        var expected = Convert.FromBase64String(Create(password, decoded[..SaltLength]));
        return CryptographicOperations.FixedTimeEquals(expected, decoded);
    }
}
