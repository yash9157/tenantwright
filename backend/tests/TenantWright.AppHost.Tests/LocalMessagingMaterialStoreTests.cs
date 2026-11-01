using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Extensions.Options;
using Shared.EventBus.Messaging;
using Xunit;

namespace TenantWright.AppHost.Tests;

public sealed class LocalMessagingMaterialStoreTests : IDisposable
{
    private static readonly string[] Principals =
    [
        TenantWrightMessagingTopology.AuditLogsPrincipal,
        TenantWrightMessagingTopology.AuthPrincipal,
        TenantWrightMessagingTopology.IdentityPrincipal,
        TenantWrightMessagingTopology.NotificationsPrincipal,
        TenantWrightMessagingTopology.PermissionsPrincipal,
        TenantWrightMessagingTopology.TenantPrincipal,
        TenantWrightMessagingTopology.WorkflowsPrincipal
    ];

    private readonly TemporaryDirectory _root = new();

    public void Dispose() => _root.Dispose();

    [Fact]
    public void Ensure_should_generate_a_distinct_password_and_p256_key_pair_per_principal()
    {
        var material = LocalMessagingMaterialStore.Ensure(_root.Path, Principals);

        Assert.Equal(Principals, material.Principals.Select(item => item.Principal));
        Assert.Matches("^[0-9a-f]{64}$", material.BrokerAdminPassword);
        Assert.Equal(Principals.Length + 1, material.Principals.Select(item => item.BrokerPassword).Append(material.BrokerAdminPassword).Distinct().Count());
        Assert.Equal(Principals.Length, material.Principals.Select(item => item.KeyId).Distinct().Count());
        foreach (var principal in material.Principals)
        {
            Assert.Matches("^[0-9a-f]{64}$", principal.BrokerPassword);
            Assert.Matches($"^{principal.Principal}-local-[0-9a-f]{{16}}$", principal.KeyId);
            Assert.StartsWith(_root.Path, principal.PrivateKeyPath, StringComparison.Ordinal);
            Assert.True(Path.IsPathFullyQualified(principal.PublicKeyPath));
            AssertReadOnly(principal.PrivateKeyPath);
            AssertReadOnly(principal.PublicKeyPath);
            using var key = ECDsa.Create();
            key.ImportFromPem(File.ReadAllText(principal.PrivateKeyPath));
            Assert.Equal(256, key.KeySize);
            Assert.DoesNotContain(principal.BrokerPassword, principal.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Ensure_should_reuse_existing_material_without_rewriting_it()
    {
        var first = LocalMessagingMaterialStore.Ensure(_root.Path, Principals);
        var snapshot = Snapshot();

        var second = LocalMessagingMaterialStore.Ensure(_root.Path, Principals);

        Assert.Equal(first.BrokerAdminPassword, second.BrokerAdminPassword);
        Assert.Equal(
            first.Principals.Select(item => (item.Principal, item.BrokerPassword, item.KeyId, item.PrivateKeyPath, item.PublicKeyPath)),
            second.Principals.Select(item => (item.Principal, item.BrokerPassword, item.KeyId, item.PrivateKeyPath, item.PublicKeyPath)));
        Assert.Equal(snapshot, Snapshot());
    }

    [Fact]
    public void Ensure_should_add_material_for_a_new_principal_and_keep_the_existing_ones()
    {
        var first = LocalMessagingMaterialStore.Ensure(_root.Path, Principals[..2]);

        var second = LocalMessagingMaterialStore.Ensure(_root.Path, Principals);

        Assert.Equal(first.Principals[0].KeyId, second.For(Principals[0]).KeyId);
        Assert.Equal(first.Principals[1].BrokerPassword, second.For(Principals[1]).BrokerPassword);
        Assert.Equal(Principals.Length, second.Principals.Count);
    }

    [Fact]
    public void Generated_material_should_satisfy_the_messaging_validator_signer_and_key_registry()
    {
        var material = LocalMessagingMaterialStore.Ensure(_root.Path, Principals);
        var validator = new MessagingOptionsValidator(new DevelopmentHostEnvironment());

        foreach (var principal in material.Principals)
        {
            var options = OptionsFor(material, principal);
            var result = validator.Validate(null, options);
            Assert.True(result.Succeeded, result.FailureMessage);

            using var signer = new TenantWrightMessageSigner(Options.Create(options));
            using var registry = new TenantWrightPublicKeyRegistry(Options.Create(options));
            var payload = "local-material-round-trip"u8.ToArray();
            var signature = signer.Sign(payload);
            Assert.True(registry.Verify(principal.KeyId, principal.Principal, payload, signature));
            var other = material.Principals.First(item => item.Principal != principal.Principal);
            Assert.False(registry.Verify(other.KeyId, other.Principal, payload, signature));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\n")]
    public void Ensure_should_reject_a_corrupt_password_file_without_overwriting_it(string corrupt)
    {
        LocalMessagingMaterialStore.Ensure(_root.Path, Principals);
        var path = Path.Combine(_root.Path, "principals", Principals[0], "broker.password");
        Overwrite(path, corrupt);

        var exception = Assert.Throws<InvalidOperationException>(() => LocalMessagingMaterialStore.Ensure(_root.Path, Principals));

        Assert.Contains(path, exception.Message, StringComparison.Ordinal);
        Assert.Equal(corrupt, File.ReadAllText(path));
    }

    [Fact]
    public void Ensure_should_reject_a_corrupt_admin_password_file()
    {
        LocalMessagingMaterialStore.Ensure(_root.Path, Principals);
        Overwrite(Path.Combine(_root.Path, "broker-admin.password"), "not-a-password");

        Assert.Throws<InvalidOperationException>(() => LocalMessagingMaterialStore.Ensure(_root.Path, Principals));
    }

    [Fact]
    public void Ensure_should_reject_a_corrupt_private_key()
    {
        var material = LocalMessagingMaterialStore.Ensure(_root.Path, Principals);
        var path = material.Principals[0].PrivateKeyPath;
        Overwrite(path, "-----BEGIN PRIVATE KEY-----\nbm90IGEga2V5\n-----END PRIVATE KEY-----\n");

        var exception = Assert.Throws<InvalidOperationException>(() => LocalMessagingMaterialStore.Ensure(_root.Path, Principals));

        Assert.Contains(path, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ensure_should_reject_a_private_key_on_another_curve()
    {
        var material = LocalMessagingMaterialStore.Ensure(_root.Path, Principals);
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Overwrite(material.Principals[0].PrivateKeyPath, p384.ExportPkcs8PrivateKeyPem());

        Assert.Throws<InvalidOperationException>(() => LocalMessagingMaterialStore.Ensure(_root.Path, Principals));
    }

    [Fact]
    public void Ensure_should_reject_a_public_key_that_does_not_match_its_private_key()
    {
        var material = LocalMessagingMaterialStore.Ensure(_root.Path, Principals);
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Overwrite(material.Principals[0].PublicKeyPath, stranger.ExportSubjectPublicKeyInfoPem());

        Assert.Throws<InvalidOperationException>(() => LocalMessagingMaterialStore.Ensure(_root.Path, Principals));
    }

    [Fact]
    public void Ensure_should_never_accept_a_private_key_as_the_public_key()
    {
        var material = LocalMessagingMaterialStore.Ensure(_root.Path, Principals);
        Overwrite(material.Principals[0].PublicKeyPath, File.ReadAllText(material.Principals[0].PrivateKeyPath));

        Assert.Throws<InvalidOperationException>(() => LocalMessagingMaterialStore.Ensure(_root.Path, Principals));
    }

    [Fact]
    public void Ensure_should_reject_a_public_key_without_its_private_key()
    {
        var material = LocalMessagingMaterialStore.Ensure(_root.Path, Principals);
        Delete(material.Principals[0].PrivateKeyPath);

        Assert.Throws<InvalidOperationException>(() => LocalMessagingMaterialStore.Ensure(_root.Path, Principals));
        Assert.False(File.Exists(material.Principals[0].PrivateKeyPath));
    }

    [Fact]
    public void Ensure_should_restore_a_missing_public_key_from_its_private_key()
    {
        var material = LocalMessagingMaterialStore.Ensure(_root.Path, Principals);
        var original = File.ReadAllText(material.Principals[0].PublicKeyPath);
        Delete(material.Principals[0].PublicKeyPath);

        var restored = LocalMessagingMaterialStore.Ensure(_root.Path, Principals);

        Assert.Equal(original, File.ReadAllText(restored.Principals[0].PublicKeyPath));
        Assert.Equal(material.Principals[0].KeyId, restored.Principals[0].KeyId);
    }

    [Fact]
    public void Ensure_should_repair_lost_file_protection()
    {
        var material = LocalMessagingMaterialStore.Ensure(_root.Path, Principals);
        var path = material.Principals[0].PrivateKeyPath;
        MakeWritable(path);

        LocalMessagingMaterialStore.Ensure(_root.Path, Principals);

        AssertReadOnly(path);
    }

    [Theory]
    [InlineData("guest")]
    [InlineData("tenantwright-")]
    [InlineData("tenantwright-Auth")]
    [InlineData("tenantwright-../escape")]
    public void Ensure_should_reject_invalid_principal_names(string principal)
    {
        Assert.Throws<InvalidOperationException>(() => LocalMessagingMaterialStore.Ensure(_root.Path, [principal]));
        Assert.False(Directory.Exists(Path.Combine(_root.Path, "principals")));
    }

    [Fact]
    public void Ensure_should_reject_duplicate_principals_and_relative_roots()
    {
        Assert.Throws<InvalidOperationException>(() => LocalMessagingMaterialStore.Ensure(_root.Path, [Principals[0], Principals[0]]));
        Assert.Throws<InvalidOperationException>(() => LocalMessagingMaterialStore.Ensure("relative/material", Principals));
    }

    [Fact]
    public void For_should_reject_an_unknown_principal()
    {
        var material = LocalMessagingMaterialStore.Ensure(_root.Path, Principals[..1]);

        Assert.Throws<InvalidOperationException>(() => material.For(Principals[1]));
    }

    [Fact]
    public void Secret_files_should_be_readable_only_by_the_current_user()
    {
        var material = LocalMessagingMaterialStore.Ensure(_root.Path, Principals);
        string[] secrets =
        [
            Path.Combine(_root.Path, "broker-admin.password"),
            Path.Combine(_root.Path, "principals", Principals[0], "broker.password"),
            material.Principals[0].PrivateKeyPath
        ];

        foreach (var path in secrets)
        {
            if (OperatingSystem.IsWindows())
            {
                AssertOwnerOnlyAcl(path);
            }
            else
            {
                Assert.Equal(UnixFileMode.UserRead, File.GetUnixFileMode(path));
            }
        }

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                File.GetUnixFileMode(material.Principals[0].PublicKeyPath));
        }
    }

    [Fact]
    public void Ensure_should_leave_no_temporary_files_behind()
    {
        LocalMessagingMaterialStore.Ensure(_root.Path, Principals);

        Assert.Empty(Directory.EnumerateFiles(_root.Path, "*.tmp", SearchOption.AllDirectories));
    }

    private static MessagingOptions OptionsFor(LocalMessagingMaterial material, LocalMessagingPrincipalMaterial principal) => new()
    {
        Provider = TenantWrightMessagingProvider.Wolverine,
        ServiceName = principal.Principal,
        ProducerPrincipal = principal.Principal,
        RabbitMq = new MessagingRabbitMqOptions
        {
            HostName = "localhost",
            Port = 5672,
            VirtualHost = "tenantwright-development-wolverine",
            UserName = principal.Principal,
            Password = principal.BrokerPassword,
            UseTls = false,
            AllowInsecureTransport = true
        },
        Trust = new MessagingTrustOptions
        {
            ActiveKeyId = principal.KeyId,
            PrivateKeyPath = principal.PrivateKeyPath,
            PublicKeys = material.Principals
                .Select(item => new MessagingPublicKeyOptions
                {
                    KeyId = item.KeyId,
                    Actor = item.Principal,
                    PublicKeyPath = item.PublicKeyPath
                })
                .ToList()
        }
    };

    private Dictionary<string, (string Contents, DateTime LastWriteUtc)> Snapshot()
        => Directory.EnumerateFiles(_root.Path, "*", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith(".lock", StringComparison.Ordinal))
            .ToDictionary(path => path, path => (File.ReadAllText(path), File.GetLastWriteTimeUtc(path)), StringComparer.Ordinal);

    private static void Overwrite(string path, string contents)
    {
        MakeWritable(path);
        File.WriteAllText(path, contents);
    }

    private static void Delete(string path)
    {
        MakeWritable(path);
        File.Delete(path);
    }

    private static void MakeWritable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
        else
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static void AssertReadOnly(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.True((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0, $"{path} must be read-only.");
        }
        else
        {
            Assert.Equal(UnixFileMode.None, File.GetUnixFileMode(path) & (UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite));
        }
    }

    [SupportedOSPlatform("windows")]
    private static void AssertOwnerOnlyAcl(string path)
    {
        var security = new FileInfo(path).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected, $"{path} must not inherit access rules.");
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
        using var identity = WindowsIdentity.GetCurrent();
        var rule = Assert.Single(rules);
        Assert.Equal(identity.User, rule.IdentityReference);
        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
    }
}
