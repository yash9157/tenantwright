using System.Security.Cryptography;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Shared.EventBus.Messaging;
using Shared.EventContracts;
using Shared.Events;
using Shared.MultiTenancy;

namespace Shared.EventBus.Tests;

internal sealed class MessagingTestKeyMaterial : IDisposable
{
    private readonly string _directory;

    public MessagingTestKeyMaterial()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"tenantwright-messaging-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        PrivateKeyPath = Path.Combine(_directory, "signing-private.pem");
        PublicKeyPath = Path.Combine(_directory, "signing-public.pem");

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        WriteReadOnly(PrivateKeyPath, key.ExportPkcs8PrivateKeyPem());
        WriteReadOnly(PublicKeyPath, key.ExportSubjectPublicKeyInfoPem());
    }

    public string PrivateKeyPath { get; }
    public string PublicKeyPath { get; }

    public void MakePrivateKeyWritable() =>
        File.SetAttributes(PrivateKeyPath, File.GetAttributes(PrivateKeyPath) & ~FileAttributes.ReadOnly);

    public void Dispose()
    {
        foreach (var path in new[] { PrivateKeyPath, PublicKeyPath })
        {
            if (!File.Exists(path))
                continue;
            File.SetAttributes(path, FileAttributes.Normal);
        }
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static void WriteReadOnly(string path, string content)
    {
        File.WriteAllText(path, content);
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
    }
}

internal sealed class MessagingTestSigner(string actor, string keyId) : ITenantWrightMessageSigner, IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public string Actor { get; } = actor;
    public string KeyId { get; } = keyId;

    public byte[] Sign(ReadOnlySpan<byte> canonicalInput) =>
        _key.SignData(canonicalInput, HashAlgorithmName.SHA256);

    public bool Verify(ReadOnlySpan<byte> canonicalInput, ReadOnlySpan<byte> signature) =>
        _key.VerifyData(canonicalInput, signature, HashAlgorithmName.SHA256);

    public void Dispose() => _key.Dispose();
}

internal sealed class MessagingTestPublicKeyRegistry(params MessagingTestSigner[] signers) : ITenantWrightPublicKeyRegistry
{
    private readonly Dictionary<string, MessagingTestSigner> _signers =
        signers.ToDictionary(signer => signer.KeyId, StringComparer.Ordinal);

    public bool Verify(string keyId, string actor, ReadOnlySpan<byte> canonicalInput, ReadOnlySpan<byte> signature) =>
        _signers.TryGetValue(keyId, out var signer) &&
        string.Equals(signer.Actor, actor, StringComparison.Ordinal) &&
        signer.Verify(canonicalInput, signature);
}

internal sealed class FixedMessagingTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;
}

internal sealed class MessagingTestHostEnvironment(string environmentName) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = environmentName;
    public string ApplicationName { get; set; } = "Shared.EventBus.Tests";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

internal sealed class MessagingTestCurrentTenant(Guid? initialTenantId = null) : ICurrentTenant
{
    private readonly AsyncLocal<Guid?> _tenantId = new();

    public bool IsAvailable => Id.HasValue;
    public Guid? Id => _tenantId.Value ?? initialTenantId;
    public string? Name => null;

    public IDisposable Change(Guid? id, string? name = null)
    {
        var previous = _tenantId.Value;
        _tenantId.Value = id;
        return new RestoreScope(() => _tenantId.Value = previous);
    }

    private sealed class RestoreScope(Action restore) : IDisposable
    {
        private Action? _restore = restore;
        public void Dispose() => Interlocked.Exchange(ref _restore, null)?.Invoke();
    }
}

internal static class MessagingTestData
{
    public const string IdentityPrincipal = "tenantwright-identity";
    public const string NotificationsPrincipal = "tenantwright-notifications";
    public const string WorkflowsEndpoint = "tenantwright-workflows";
    public const string NotificationsEndpoint = "tenantwright-notifications";

    public static MessagingOptions CreateValidOptions(MessagingTestKeyMaterial keys, string principal = NotificationsPrincipal) => new()
    {
        Provider = TenantWrightMessagingProvider.MassTransit,
        ServiceName = principal,
        ProducerPrincipal = principal,
        RabbitMq = new MessagingRabbitMqOptions
        {
            HostName = "localhost",
            Port = 5672,
            VirtualHost = "/tenantwright-phase2-tests",
            UserName = principal,
            Password = "fixed-test-credential",
            UseTls = false,
            AllowInsecureTransport = true,
            PublisherConfirmation = true,
            DetectUnroutableMessages = true,
            PrefetchCount = 8,
            ConcurrentMessageLimit = 4
        },
        Trust = new MessagingTrustOptions
        {
            ActiveKeyId = "test-key-1",
            PrivateKeyPath = keys.PrivateKeyPath,
            MaximumOriginAge = TimeSpan.FromHours(24),
            MaximumFutureClockSkew = TimeSpan.FromMinutes(2),
            MaximumRelayLifetime = TimeSpan.FromMinutes(15),
            PublicKeys =
            [
                new MessagingPublicKeyOptions
                {
                    KeyId = "test-key-1",
                    Actor = principal,
                    PublicKeyPath = keys.PublicKeyPath
                }
            ]
        }
    };

    public static TenantWrightMessageCatalog CreateCatalog(
        IEnumerable<TenantWrightReceiveEndpoint>? receiveEndpoints = null,
        IReadOnlySet<string>? identityOrigins = null)
    {
        var routes = new[]
        {
            new TenantWrightContractRoute(
                typeof(TenantDeactivatedEto),
                TenantWrightMessageRouteKind.Event,
                null,
                identityOrigins ?? new HashSet<string>(StringComparer.Ordinal) { IdentityPrincipal }),
            new TenantWrightContractRoute(
                typeof(NotificationSendRequestedTenantEto),
                TenantWrightMessageRouteKind.Command,
                NotificationsEndpoint,
                new HashSet<string>(StringComparer.Ordinal) { WorkflowsEndpoint })
        };
        var endpoints = new[]
        {
            new TenantWrightEndpointRoute(
                WorkflowsEndpoint,
                WorkflowsEndpoint,
                new[] { typeof(TenantDeactivatedEto) }),
            new TenantWrightEndpointRoute(
                NotificationsEndpoint,
                NotificationsPrincipal,
                new[] { typeof(NotificationSendRequestedTenantEto) })
        };
        return new TenantWrightMessageCatalog(routes, endpoints, receiveEndpoints ?? []);
    }

    public static DistributedContractAttribute Metadata<T>() where T : class =>
        typeof(T).GetCustomAttributes(typeof(DistributedContractAttribute), inherit: false)
            .Cast<DistributedContractAttribute>()
            .Single();
}
