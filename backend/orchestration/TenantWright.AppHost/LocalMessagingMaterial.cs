using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace TenantWright.AppHost;

/// <summary>One messaging principal's local broker credential and ECDSA P-256 signing identity.</summary>
/// <param name="Principal">The broker user, producer principal and trust actor.</param>
/// <param name="BrokerPassword">The principal's RabbitMQ password.</param>
/// <param name="KeyId">Immutable key ID derived from the public key, so regenerated keys never reuse an ID.</param>
/// <param name="PrivateKeyPath">Absolute path of the read-only, owner-only PKCS#8 PEM private key.</param>
/// <param name="PublicKeyPath">Absolute path of the read-only SubjectPublicKeyInfo PEM public key.</param>
internal sealed record LocalMessagingPrincipalMaterial(
    string Principal,
    string BrokerPassword,
    string KeyId,
    string PrivateKeyPath,
    string PublicKeyPath)
{
    // The password must never reach logs or exception text through record formatting.
    public override string ToString() => $"{Principal} ({KeyId})";
}

/// <summary>The complete local messaging material: the broker administrator password and every principal.</summary>
internal sealed class LocalMessagingMaterial
{
    public LocalMessagingMaterial(
        string rootDirectory,
        string brokerAdminPassword,
        IReadOnlyList<LocalMessagingPrincipalMaterial> principals)
    {
        RootDirectory = rootDirectory;
        BrokerAdminPassword = brokerAdminPassword;
        Principals = principals;
    }

    public string RootDirectory { get; }

    public string BrokerAdminPassword { get; }

    /// <summary>Every principal, in the order requested.</summary>
    public IReadOnlyList<LocalMessagingPrincipalMaterial> Principals { get; }

    public LocalMessagingPrincipalMaterial For(string principal)
        => Principals.SingleOrDefault(item => string.Equals(item.Principal, principal, StringComparison.Ordinal))
           ?? throw new InvalidOperationException($"Unknown local messaging principal '{principal}'.");

    public override string ToString() => $"Local messaging material at '{RootDirectory}'";
}

/// <summary>
/// Generates local messaging material on first use and reuses it afterwards. Layout under the root directory:
/// <code>
/// broker-admin.password                         RabbitMQ administrator password (owner-only, read-only)
/// principals/{principal}/broker.password        principal's RabbitMQ password (owner-only, read-only)
/// principals/{principal}/signing-key.pem        PKCS#8 ECDSA P-256 private key (owner-only, read-only)
/// principals/{principal}/signing-key.public.pem SubjectPublicKeyInfo public key (read-only)
/// </code>
/// </summary>
/// <remarks>
/// Files are created once and never rewritten: an existing file is validated and reused, a missing file is
/// generated (so a principal added to the topology gets its material on the next start), and a malformed file fails
/// the AppHost with the offending path rather than being silently replaced, because the rest of the local system
/// (broker users, persisted outbox signatures) was built from it. A cross-process lock serializes AppHost starts.
/// </remarks>
internal static partial class LocalMessagingMaterialStore
{
    private const string AdminPasswordFileName = "broker-admin.password";
    private const string PrincipalsDirectoryName = "principals";
    private const string BrokerPasswordFileName = "broker.password";
    private const string PrivateKeyFileName = "signing-key.pem";
    private const string PublicKeyFileName = "signing-key.public.pem";
    private const string LockFileName = ".lock";
    private const string PrivateKeyLabel = "PRIVATE KEY";
    private const string PublicKeyLabel = "PUBLIC KEY";
    private const string P256Oid = "1.2.840.10045.3.1.7";
    private const int PasswordBytes = 32;
    private const int PasswordLength = PasswordBytes * 2;
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);

    public static LocalMessagingMaterial Ensure(string rootDirectory, IReadOnlyList<string> principals)
    {
        ArgumentNullException.ThrowIfNull(principals);
        if (!Path.IsPathFullyQualified(rootDirectory))
        {
            throw new InvalidOperationException($"The local messaging material directory '{rootDirectory}' must be an absolute path.");
        }

        if (principals.Count == 0 || principals.Distinct(StringComparer.Ordinal).Count() != principals.Count)
        {
            throw new InvalidOperationException("Local messaging material requires a non-empty list of distinct principals.");
        }

        foreach (var principal in principals)
        {
            if (!PrincipalPattern().IsMatch(principal))
            {
                throw new InvalidOperationException($"'{principal}' is not a lowercase 'tenantwright-<service>' messaging principal.");
            }
        }

        var root = Path.GetFullPath(rootDirectory);
        LocalSecretFile.EnsureDirectory(root);
        using var materialLock = AcquireLock(Path.Combine(root, LockFileName));

        var adminPassword = EnsurePassword(root, Path.Combine(root, AdminPasswordFileName));
        var materials = new List<LocalMessagingPrincipalMaterial>(principals.Count);
        foreach (var principal in principals)
        {
            var directory = Path.Combine(root, PrincipalsDirectoryName, principal);
            LocalSecretFile.EnsureDirectory(directory);
            var password = EnsurePassword(root, Path.Combine(directory, BrokerPasswordFileName));
            var privateKeyPath = Path.Combine(directory, PrivateKeyFileName);
            var publicKeyPath = Path.Combine(directory, PublicKeyFileName);
            var keyId = EnsureKeyPair(root, principal, privateKeyPath, publicKeyPath);
            materials.Add(new LocalMessagingPrincipalMaterial(principal, password, keyId, privateKeyPath, publicKeyPath));
        }

        return new LocalMessagingMaterial(root, adminPassword, materials);
    }

    private static string EnsurePassword(string root, string path)
    {
        if (!File.Exists(path))
        {
            var generated = Encoding.ASCII.GetBytes(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(PasswordBytes)));
            try
            {
                LocalSecretFile.TryCreate(path, generated, LocalSecretFileAccess.OwnerOnly);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(generated);
            }
        }

        var stored = File.ReadAllText(path, Encoding.ASCII);
        if (stored.Length != PasswordLength || !stored.All(char.IsAsciiHexDigitLower))
        {
            throw Corrupt(root, path, $"expected exactly {PasswordLength} lowercase hexadecimal characters");
        }

        LocalSecretFile.Protect(path, LocalSecretFileAccess.OwnerOnly);
        return stored;
    }

    /// <summary>Ensures a valid P-256 key pair and returns its fingerprint-derived key ID.</summary>
    private static string EnsureKeyPair(string root, string principal, string privateKeyPath, string publicKeyPath)
    {
        if (!File.Exists(privateKeyPath))
        {
            if (File.Exists(publicKeyPath))
            {
                throw Corrupt(root, publicKeyPath, "the public key exists without its private key");
            }

            using var generated = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            CreatePem(privateKeyPath, PrivateKeyLabel, generated.ExportPkcs8PrivateKey(), LocalSecretFileAccess.OwnerOnly);
        }

        byte[] subjectPublicKeyInfo;
        using (var privateKey = LoadP256(root, privateKeyPath, PrivateKeyLabel))
        {
            subjectPublicKeyInfo = privateKey.ExportSubjectPublicKeyInfo();
        }

        if (!File.Exists(publicKeyPath))
        {
            CreatePem(publicKeyPath, PublicKeyLabel, subjectPublicKeyInfo.ToArray(), LocalSecretFileAccess.Public);
        }

        using (var publicKey = LoadP256(root, publicKeyPath, PublicKeyLabel))
        {
            if (!publicKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(subjectPublicKeyInfo))
            {
                throw Corrupt(root, publicKeyPath, "the public key does not belong to the private key next to it");
            }
        }

        LocalSecretFile.Protect(privateKeyPath, LocalSecretFileAccess.OwnerOnly);
        LocalSecretFile.Protect(publicKeyPath, LocalSecretFileAccess.Public);
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(subjectPublicKeyInfo));
        return $"{principal}-local-{fingerprint[..16]}";
    }

    private static void CreatePem(string path, string label, byte[] der, LocalSecretFileAccess access)
    {
        var pem = PemEncoding.Write(label, der);
        var bytes = Encoding.ASCII.GetBytes(pem);
        try
        {
            LocalSecretFile.TryCreate(path, bytes, access);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(der);
            Array.Clear(pem);
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary>
    /// Loads a single-PEM P-256 key whose label is exactly <paramref name="label"/>. The label check keeps a private
    /// key from ever being accepted (and then left world-readable) as a public verification key.
    /// </summary>
    private static ECDsa LoadP256(string root, string path, string label)
    {
        var isPrivate = string.Equals(label, PrivateKeyLabel, StringComparison.Ordinal);
        var bytes = File.ReadAllBytes(path);
        var pem = Encoding.ASCII.GetChars(bytes);
        byte[]? der = null;
        var key = ECDsa.Create();
        try
        {
            ReadOnlySpan<char> text = pem;
            if (!PemEncoding.TryFind(text, out var fields) ||
                !text[fields.Label].Equals(label, StringComparison.Ordinal) ||
                !text[..fields.Location.Start].IsWhiteSpace() ||
                !text[fields.Location.End..].IsWhiteSpace())
            {
                throw Corrupt(root, path, $"expected exactly one '{label}' PEM block");
            }

            var (offset, length) = fields.Base64Data.GetOffsetAndLength(pem.Length);
            der = Convert.FromBase64CharArray(pem, offset, length);
            if (isPrivate)
            {
                key.ImportPkcs8PrivateKey(der, out var read);
                RequireFullyRead(root, path, read, der.Length);
            }
            else
            {
                key.ImportSubjectPublicKeyInfo(der, out var read);
                RequireFullyRead(root, path, read, der.Length);
            }

            var curve = key.ExportParameters(includePrivateParameters: false).Curve;
            if (key.KeySize != 256 || !curve.IsNamed || !string.Equals(curve.Oid.Value, P256Oid, StringComparison.Ordinal))
            {
                throw Corrupt(root, path, "the key is not an ECDSA P-256 key");
            }

            return key;
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            key.Dispose();
            throw Corrupt(root, path, $"expected a PEM ECDSA P-256 {(isPrivate ? "private" : "public")} key");
        }
        catch
        {
            key.Dispose();
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            Array.Clear(pem);
            if (der is not null)
            {
                CryptographicOperations.ZeroMemory(der);
            }
        }
    }

    private static void RequireFullyRead(string root, string path, int read, int length)
    {
        if (read != length)
        {
            throw Corrupt(root, path, "the PEM block carries trailing data");
        }
    }

    private static FileStream AcquireLock(string path)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (stopwatch.Elapsed < LockTimeout)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(200));
            }
        }
    }

    private static InvalidOperationException Corrupt(string root, string path, string reason)
        => new(
            $"Local messaging material file '{path}' is invalid: {reason}. Nothing was overwritten. Delete the material " +
            $"directory '{root}' to regenerate all local messaging material; the local broker users and keys are " +
            "re-provisioned from it on the next AppHost start.");

    [GeneratedRegex("^tenantwright-[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex PrincipalPattern();
}
