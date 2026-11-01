using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace TenantWright.AppHost;

/// <summary>
/// Local messaging owned by the AppHost: the provider every service runs, the generated broker credentials and
/// signing keys (<see cref="LocalMessagingMaterialStore"/>), and the declarative broker provisioning
/// (<see cref="LocalBrokerDefinitions"/>) the local RabbitMQ container imports. Development run mode only.
/// </summary>
/// <remarks>
/// AppHost settings (all optional):
/// <list type="bullet">
/// <item><c>LocalMessaging:Provider</c>: <c>Wolverine</c> (default), <c>MassTransit</c> or <c>None</c>. <c>None</c>
/// runs every service bus-free and starts no broker.</item>
/// <item><c>LocalMessaging:MaterialPath</c>: absolute directory for the generated material, outside the repository.
/// Defaults to <c>{LocalApplicationData}/TenantWright/local-messaging</c>.</item>
/// <item><c>LocalMessaging:VirtualHost</c>: defaults to <c>tenantwright-development-{provider}</c>, so switching provider
/// never meets queues the other provider declared with inequivalent arguments.</item>
/// <item><c>LocalMessaging:MassTransitLicensePath</c>: required for <c>MassTransit</c>; an absolute read-only
/// licence file passed to services as <c>MT_LICENSE_PATH</c>.</item>
/// </list>
/// </remarks>
internal sealed class LocalMessaging
{
    public const string NoneProvider = "None";
    public const string MassTransitProvider = "MassTransit";
    public const string WolverineProvider = "Wolverine";

    /// <summary>The local broker administrator (dashboard, management UI, AppHost health check). Not a service principal.</summary>
    public const string BrokerAdminUserName = "local-admin";

    private const string Section = "LocalMessaging";

    /// <summary>Every <c>TenantWrightMessagingTopology</c> service principal; each gets its own broker user and key pair.</summary>
    internal static readonly string[] Principals =
    [
        "tenantwright-auditlogs",
        "tenantwright-auth",
        "tenantwright-identity",
        "tenantwright-notifications",
        "tenantwright-permissions",
        "tenantwright-tenant",
        "tenantwright-workflows"
    ];

    private readonly LocalMessagingMaterial? _material;
    private readonly LocalBrokerFiles? _brokerFiles;
    private readonly IResourceBuilder<ParameterResource>? _brokerAdminPassword;
    private readonly IReadOnlyDictionary<string, IResourceBuilder<ParameterResource>> _brokerPasswords;

    private LocalMessaging(
        string provider,
        string virtualHost,
        string? massTransitLicensePath,
        LocalMessagingMaterial? material,
        LocalBrokerFiles? brokerFiles,
        IResourceBuilder<ParameterResource>? brokerAdminPassword,
        IReadOnlyDictionary<string, IResourceBuilder<ParameterResource>> brokerPasswords)
    {
        Provider = provider;
        VirtualHost = virtualHost;
        MassTransitLicensePath = massTransitLicensePath;
        _material = material;
        _brokerFiles = brokerFiles;
        _brokerAdminPassword = brokerAdminPassword;
        _brokerPasswords = brokerPasswords;
    }

    /// <summary>The exact <c>Messaging:Provider</c> value every service receives.</summary>
    public string Provider { get; }

    public bool Enabled => !string.Equals(Provider, NoneProvider, StringComparison.Ordinal);

    public string VirtualHost { get; }

    public string? MassTransitLicensePath { get; }

    public LocalMessagingMaterial Material => _material ?? throw Disabled();

    public LocalBrokerFiles BrokerFiles => _brokerFiles ?? throw Disabled();

    public IResourceBuilder<ParameterResource> BrokerAdminPassword => _brokerAdminPassword ?? throw Disabled();

    public IResourceBuilder<ParameterResource> BrokerPassword(string principal)
        => _brokerPasswords.TryGetValue(principal, out var password)
            ? password
            : throw new InvalidOperationException($"Unknown local messaging principal '{principal}'.");

    public static LocalMessaging Create(IDistributedApplicationBuilder builder)
    {
        var configuration = builder.Configuration;
        var provider = ReadProvider(configuration);
        if (string.Equals(provider, NoneProvider, StringComparison.Ordinal))
        {
            return new LocalMessaging(provider, string.Empty, null, null, null, null,
                new Dictionary<string, IResourceBuilder<ParameterResource>>(StringComparer.Ordinal));
        }

        // Generated credentials, plaintext AMQP and insecure-transport opt-ins are local development only.
        if (!builder.Environment.IsDevelopment() || !builder.ExecutionContext.IsRunMode)
        {
            throw new InvalidOperationException(
                $"{Section}:Provider '{provider}' runs a generated local broker and is allowed only when the AppHost " +
                $"runs in Development. Set {Section}:Provider=None for any other environment or for publishing.");
        }

        var massTransitLicensePath = string.Equals(provider, MassTransitProvider, StringComparison.Ordinal)
            ? RequireReadOnlyFile(configuration, $"{Section}:MassTransitLicensePath")
            : null;
        var virtualHost = ReadVirtualHost(configuration, provider);
        var materialPath = ReadMaterialPath(configuration, builder.AppHostDirectory);

        var material = LocalMessagingMaterialStore.Ensure(materialPath, Principals);
        var brokerFiles = LocalBrokerDefinitions.Ensure(material, BrokerAdminUserName, virtualHost);

        // Secret parameters keep the passwords masked on the dashboard; they are never persisted to user secrets
        // because the material directory is their single source.
        var brokerAdminPassword = builder.AddParameter("rabbitmq-admin-password", material.BrokerAdminPassword, secret: true);
        var brokerPasswords = material.Principals.ToDictionary(
            principal => principal.Principal,
            principal => builder.AddParameter($"rabbitmq-{principal.Principal}-password", principal.BrokerPassword, secret: true),
            StringComparer.Ordinal);

        return new LocalMessaging(provider, virtualHost, massTransitLicensePath, material, brokerFiles, brokerAdminPassword, brokerPasswords);
    }

    private static string ReadProvider(ConfigurationManager configuration)
    {
        var configured = configuration[$"{Section}:Provider"]?.Trim();
        if (string.IsNullOrEmpty(configured))
        {
            return WolverineProvider;
        }

        return configured is NoneProvider or MassTransitProvider or WolverineProvider
            ? configured
            : throw new InvalidOperationException(
                $"AppHost setting '{Section}:Provider' must be exactly '{WolverineProvider}', '{MassTransitProvider}' or '{NoneProvider}'.");
    }

    private static string ReadVirtualHost(ConfigurationManager configuration, string provider)
    {
        var configured = configuration[$"{Section}:VirtualHost"]?.Trim();
        var virtualHost = string.IsNullOrEmpty(configured)
            ? $"tenantwright-development-{provider.ToLowerInvariant()}"
            : configured;

        return virtualHost.Length is > 0 and <= 64 &&
               virtualHost.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-') &&
               virtualHost[0] != '-'
            ? virtualHost
            : throw new InvalidOperationException(
                $"AppHost setting '{Section}:VirtualHost' must be 1-64 lowercase letters, digits or '-' (and not '/').");
    }

    /// <summary>
    /// The material directory: absolute, and never inside the repository working tree, so generated passwords and
    /// private keys cannot be committed.
    /// </summary>
    private static string ReadMaterialPath(ConfigurationManager configuration, string appHostDirectory)
    {
        var key = $"{Section}:MaterialPath";
        var configured = configuration[key]?.Trim();
        string path;
        if (string.IsNullOrEmpty(configured))
        {
            var localApplicationData = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolderOption.DoNotVerify);
            if (string.IsNullOrWhiteSpace(localApplicationData))
            {
                throw new InvalidOperationException(
                    $"No per-user application data directory is available; set AppHost setting '{key}' to an absolute directory.");
            }

            path = Path.Combine(localApplicationData, "TenantWright", "local-messaging");
        }
        else if (Path.IsPathFullyQualified(configured))
        {
            path = Path.GetFullPath(configured);
        }
        else
        {
            throw new InvalidOperationException($"AppHost setting '{key}' must be an absolute directory.");
        }

        var repositoryRoot = FindRepositoryRoot(appHostDirectory);
        if (repositoryRoot is not null && IsSameOrUnder(path, repositoryRoot))
        {
            throw new InvalidOperationException(
                $"AppHost setting '{key}' must point outside the repository working tree '{repositoryRoot}'.");
        }

        return path;
    }

    private static string? FindRepositoryRoot(string startDirectory)
    {
        for (var directory = new DirectoryInfo(startDirectory); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) || File.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }
        }

        return null;
    }

    private static bool IsSameOrUnder(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "." ||
               (!Path.IsPathFullyQualified(relative) &&
                relative != ".." &&
                !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    private static string RequireReadOnlyFile(ConfigurationManager configuration, string key)
    {
        var path = configuration[key]?.Trim();
        if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path) || !File.Exists(path))
        {
            throw new InvalidOperationException(
                $"AppHost setting '{key}' must identify an existing absolute file (the MassTransit provider requires a licence).");
        }

        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
            (attributes & FileAttributes.ReadOnly) == 0)
        {
            throw new InvalidOperationException($"AppHost setting '{key}' must identify a read-only regular file.");
        }

        return Path.GetFullPath(path);
    }

    private static InvalidOperationException Disabled()
        => new($"Local messaging is disabled ({Section}:Provider={NoneProvider}).");
}

internal static class LocalMessagingExtensions
{
    /// <summary>
    /// Connects a service to local messaging as <paramref name="principal"/>: its own broker user and password, its
    /// own signing key, and every principal's verification key. With messaging disabled the service runs bus-free.
    /// </summary>
    public static IResourceBuilder<ProjectResource> WithMessaging(
        this IResourceBuilder<ProjectResource> service,
        LocalInfrastructure infrastructure,
        string principal)
    {
        var messaging = infrastructure.Messaging;
        if (!messaging.Enabled)
        {
            return service.WithEnvironment("Messaging__Provider", LocalMessaging.NoneProvider);
        }

        var broker = infrastructure.RabbitMq
            ?? throw new InvalidOperationException("Local messaging is enabled but no local broker was added.");
        var identity = messaging.Material.For(principal);

        if (messaging.MassTransitLicensePath is { } licensePath)
        {
            service.WithEnvironment("MT_LICENSE_PATH", licensePath);
        }

        var endpoint = broker.Resource.PrimaryEndpoint;
        service
            .WithEnvironment("Messaging__Provider", messaging.Provider)
            .WithEnvironment("Messaging__ServiceName", principal)
            .WithEnvironment("Messaging__ProducerPrincipal", principal)
            .WithEnvironment("Messaging__RabbitMq__HostName", ReferenceExpression.Create($"{endpoint.Property(EndpointProperty.Host)}"))
            .WithEnvironment("Messaging__RabbitMq__Port", ReferenceExpression.Create($"{endpoint.Property(EndpointProperty.Port)}"))
            .WithEnvironment("Messaging__RabbitMq__VirtualHost", messaging.VirtualHost)
            .WithEnvironment("Messaging__RabbitMq__UserName", principal)
            .WithEnvironment("Messaging__RabbitMq__Password", messaging.BrokerPassword(principal))
            // Plaintext AMQP to the loopback container; the messaging validator permits it only in Development.
            .WithEnvironment("Messaging__RabbitMq__UseTls", "false")
            .WithEnvironment("Messaging__RabbitMq__AllowInsecureTransport", "true")
            .WithEnvironment("Messaging__Trust__ActiveKeyId", identity.KeyId)
            .WithEnvironment("Messaging__Trust__PrivateKeyPath", identity.PrivateKeyPath);

        var trusted = messaging.Material.Principals;
        for (var index = 0; index < trusted.Count; index++)
        {
            var key = trusted[index];
            service
                .WithEnvironment($"Messaging__Trust__PublicKeys__{index}__KeyId", key.KeyId)
                .WithEnvironment($"Messaging__Trust__PublicKeys__{index}__Actor", key.Principal)
                .WithEnvironment($"Messaging__Trust__PublicKeys__{index}__PublicKeyPath", key.PublicKeyPath)
                .WithEnvironment($"Messaging__Trust__PublicKeys__{index}__Revoked", "false");
        }

        return service.WaitFor(broker);
    }
}
