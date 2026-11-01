using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Configuration;
using Shared.Caching;
using StackExchange.Redis;
using Xunit;

namespace Shared.Valkey.IntegrationTests;

public sealed class ValkeyContainerFixture : IAsyncLifetime
{
    public const string Image =
        "valkey/valkey:9.1.1@sha256:3acc0687f2a2e1091fae6450d7842dd658c941338cf0a873ddd9e14b9e4ea4dd";

    private readonly string _password = CreateSecret();
    private readonly string _replicationPassword = CreateSecret();
    private IContainer? _container;
    private string? _certificateDirectory;

    public string KeyPrefix { get; } = $"tenantwright:integration:{Guid.NewGuid():N}:";

    public async Task InitializeAsync()
    {
        _certificateDirectory = CreateCertificateDirectory(
            _password,
            _replicationPassword);
        _container = BuildContainer(_certificateDirectory);
        await _container.StartAsync();

        await using var connection = await CreateConnectionAsync();
        await connection.GetDatabase().PingAsync();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }

        if (!string.IsNullOrWhiteSpace(_certificateDirectory) &&
            Directory.Exists(_certificateDirectory))
        {
            Directory.Delete(_certificateDirectory, recursive: true);
        }
    }

    public async Task<IConnectionMultiplexer> CreateConnectionAsync()
    {
        var configuration = CreateConfiguration("default", _password);
        return await ConnectionMultiplexer.ConnectAsync(
            ValkeyConnectionConfiguration.Create(configuration));
    }

    public async Task<string> ExecuteAsReplicationUserAsync(params string[] command)
    {
        if (_container is null || string.IsNullOrWhiteSpace(_certificateDirectory))
        {
            throw new InvalidOperationException("The Valkey fixture has not started.");
        }

        using var trustedRoot = X509CertificateLoader.LoadCertificateFromFile(
            Path.Combine(_certificateDirectory, "ca.crt"));
        using var client = new TcpClient();
        await client.ConnectAsync(
            IPAddress.Loopback,
            _container.GetMappedPublicPort(6379));
        using var tls = new SslStream(
            client.GetStream(),
            leaveInnerStreamOpen: false,
            (_, certificate, _, policyErrors) =>
                ValidateCertificate(certificate, policyErrors, trustedRoot));
        await tls.AuthenticateAsClientAsync("localhost");
        using var reader = new StreamReader(tls, Encoding.UTF8, leaveOpen: true);
        using var writer = new StreamWriter(tls, new UTF8Encoding(false), leaveOpen: true)
        {
            NewLine = "\r\n",
            AutoFlush = true
        };

        await WriteRespCommandAsync(
            writer,
            ["AUTH", "replication", _replicationPassword]);
        var authentication = await reader.ReadLineAsync();
        if (!string.Equals(authentication, "+OK", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The Valkey replication-user authentication probe failed.");
        }

        await WriteRespCommandAsync(writer, command);
        return await reader.ReadLineAsync()
               ?? throw new InvalidOperationException(
                   "Valkey closed the replication-user probe without a response.");
    }

    public IConfiguration CreateConfiguration() =>
        CreateConfiguration("default", _password);

    private IConfiguration CreateConfiguration(string user, string password)
    {
        if (_container is null || string.IsNullOrWhiteSpace(_certificateDirectory))
        {
            throw new InvalidOperationException("The Valkey fixture has not started.");
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Caching:Provider"] = "Valkey",
                ["Caching:Valkey:Configuration"] =
                    $"127.0.0.1:{_container.GetMappedPublicPort(6379)},ssl=True,sslHost=localhost,abortConnect=False,connectRetry=3,connectTimeout=5000,syncTimeout=5000",
                ["Caching:Valkey:User"] = user,
                ["Caching:Valkey:Password"] = password,
                ["Caching:Valkey:TrustedCaCertificatePath"] =
                    Path.Combine(_certificateDirectory, "ca.crt")
            })
            .Build();
    }

    public async Task<ValkeyContainerFixture> StartIndependentAsync()
    {
        var fixture = new ValkeyContainerFixture();
        await fixture.InitializeAsync();
        return fixture;
    }

    public Task StopAsync()
    {
        if (_container is null)
        {
            throw new InvalidOperationException("The Valkey fixture has not started.");
        }

        return _container.StopAsync();
    }

    private static IContainer BuildContainer(string certificateDirectory)
    {
        return new ContainerBuilder(Image)
            .WithPortBinding(6379, assignRandomHostPort: true)
            .WithBindMount(certificateDirectory, "/valkey-config", AccessMode.ReadOnly)
            .WithCommand("valkey-server", "/valkey-config/valkey.conf")
            .WithWaitStrategy(
                Wait.ForUnixContainer()
                    .UntilMessageIsLogged("Ready to accept connections"))
            .Build();
    }

    private static string CreateCertificateDirectory(
        string password,
        string replicationPassword)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"tenantwright-valkey-integration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rootRequest = new CertificateRequest(
            "CN=TenantWright Valkey Integration Root",
            rootKey,
            HashAlgorithmName.SHA256);
        rootRequest.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign |
                X509KeyUsageFlags.CrlSign |
                X509KeyUsageFlags.DigitalSignature,
                true));
        rootRequest.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(rootRequest.PublicKey, false));
        using var root = rootRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddDays(7));

        using var serverKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var serverRequest = new CertificateRequest(
            "CN=localhost",
            serverKey,
            HashAlgorithmName.SHA256);
        serverRequest.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, true));
        serverRequest.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature |
                X509KeyUsageFlags.KeyEncipherment,
                true));
        serverRequest.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection
                {
                    new("1.3.6.1.5.5.7.3.1")
                },
                true));
        var subjectAlternativeNames = new SubjectAlternativeNameBuilder();
        subjectAlternativeNames.AddDnsName("localhost");
        subjectAlternativeNames.AddIpAddress(IPAddress.Loopback);
        serverRequest.CertificateExtensions.Add(subjectAlternativeNames.Build());

        var serialNumber = RandomNumberGenerator.GetBytes(16);
        using var issuedServer = serverRequest.Create(
            root,
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddDays(2),
            serialNumber);
        using var server = issuedServer.CopyWithPrivateKey(serverKey);

        File.WriteAllText(Path.Combine(directory, "ca.crt"), root.ExportCertificatePem());
        File.WriteAllText(Path.Combine(directory, "server.crt"), server.ExportCertificatePem());
        File.WriteAllText(Path.Combine(directory, "server.key"), serverKey.ExportPkcs8PrivateKeyPem());
        File.WriteAllText(
            Path.Combine(directory, "users.acl"),
            $"""
             user default on #{HashSecret(password)} ~* &* +@all -@admin -@dangerous
             user replication on #{HashSecret(replicationPassword)} ~* &* -@all +psync +replconf +ping
             """);
        File.WriteAllText(
            Path.Combine(directory, "valkey.conf"),
            $"""
             port 0
             tls-port 6379
             tls-cert-file /valkey-config/server.crt
             tls-key-file /valkey-config/server.key
             tls-ca-cert-file /valkey-config/ca.crt
             tls-auth-clients no
             aclfile /valkey-config/users.acl
             protected-mode yes
             bind 0.0.0.0
             save ""
             appendonly no
             """);

        return directory;
    }

    private static string CreateSecret()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(48))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string HashSecret(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)))
            .ToLowerInvariant();

    private static async Task WriteRespCommandAsync(
        StreamWriter writer,
        string[] command)
    {
        await writer.WriteAsync($"*{command.Length}\r\n");
        foreach (var argument in command)
        {
            await writer.WriteAsync(
                $"${Encoding.UTF8.GetByteCount(argument)}\r\n{argument}\r\n");
        }
    }

    private static bool ValidateCertificate(
        X509Certificate? certificate,
        SslPolicyErrors policyErrors,
        X509Certificate2 trustedRoot)
    {
        if (certificate is null ||
            policyErrors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable) ||
            policyErrors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
        {
            return false;
        }

        using var serverCertificate =
            X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(trustedRoot);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        return chain.Build(serverCertificate);
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ValkeyCollection : ICollectionFixture<ValkeyContainerFixture>
{
    public const string Name = "Real Valkey";
}
