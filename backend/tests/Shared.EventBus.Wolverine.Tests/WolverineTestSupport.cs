using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shared.EventBus.Messaging;
using Shared.EventContracts;
using Shared.MultiTenancy;

namespace Shared.EventBus.Wolverine.Tests;

/// <summary>
/// The test catalog: <c>tenantwright-wolverine-alpha</c> produces a tenant command, a host-only command whose destination
/// nobody binds, and a tenant event that <c>tenantwright-wolverine-beta</c> consumes on two endpoints.
/// <c>tenantwright-wolverine-gamma</c> holds a trusted signing key but is never an approved origin in the consumer's catalog.
/// </summary>
internal static class WolverineTestTopology
{
    public const string Producer = "tenantwright-wolverine-alpha";
    public const string Consumer = "tenantwright-wolverine-beta";
    public const string Outsider = "tenantwright-wolverine-gamma";

    public const string CommandEndpoint = "tenantwright-wolverine-beta-commands-v1";
    public const string UnboundCommandEndpoint = "tenantwright-wolverine-beta-unbound-v1";
    public const string FirstEventEndpoint = "tenantwright-wolverine-beta-grants-one-v1";
    public const string SecondEventEndpoint = "tenantwright-wolverine-beta-grants-two-v1";

    public static readonly string GrantsExchange = TenantWrightMessageContracts.GetIdentity(typeof(PermissionGrantsChangedTenantEto));

    public static IReadOnlyList<string> Principals { get; } = [Producer, Consumer, Outsider];

    /// <param name="receiveEndpoints">The endpoints the composed host consumes.</param>
    /// <param name="outsiderMayPublishGrants">
    /// Only the outsider's own catalog sets this, so the outsider can stamp an event the consumer's catalog refuses.
    /// </param>
    public static TenantWrightMessageCatalog Create(
        IReadOnlyCollection<string> receiveEndpoints,
        bool outsiderMayPublishGrants = false)
    {
        var grantOrigins = new HashSet<string>(StringComparer.Ordinal) { Producer };
        if (outsiderMayPublishGrants)
            grantOrigins.Add(Outsider);

        var endpointRoutes = new[]
        {
            new TenantWrightEndpointRoute(CommandEndpoint, Consumer, [typeof(NotificationSendRequestedTenantEto)]),
            new TenantWrightEndpointRoute(UnboundCommandEndpoint, Consumer, [typeof(NotificationSendRequestedHostEto)]),
            new TenantWrightEndpointRoute(FirstEventEndpoint, Consumer, [typeof(PermissionGrantsChangedTenantEto)]),
            new TenantWrightEndpointRoute(SecondEventEndpoint, Consumer, [typeof(PermissionGrantsChangedTenantEto)])
        };

        return new TenantWrightMessageCatalog(
            [
                new TenantWrightContractRoute(typeof(NotificationSendRequestedTenantEto), TenantWrightMessageRouteKind.Command,
                    CommandEndpoint, new HashSet<string>(StringComparer.Ordinal) { Producer }),
                new TenantWrightContractRoute(typeof(NotificationSendRequestedHostEto), TenantWrightMessageRouteKind.Command,
                    UnboundCommandEndpoint, new HashSet<string>(StringComparer.Ordinal) { Producer }),
                new TenantWrightContractRoute(typeof(PermissionGrantsChangedTenantEto), TenantWrightMessageRouteKind.Event,
                    null, grantOrigins)
            ],
            endpointRoutes,
            endpointRoutes
                .Where(endpoint => receiveEndpoints.Contains(endpoint.Name))
                .Select(endpoint => new TenantWrightReceiveEndpoint(endpoint.Name, endpoint.OwnerPrincipal, endpoint.ContractTypes)));
    }

    public static TenantWrightMessageCatalog ConsumerCatalog() =>
        Create([CommandEndpoint, FirstEventEndpoint, SecondEventEndpoint]);

    public static TenantWrightMessageCatalog ProducerCatalog() => Create([]);
}

/// <summary>Where a composed test host connects. One isolated vhost per test.</summary>
public sealed record WolverineBrokerTarget(string HostName, int Port, string VirtualHost, string Password);

/// <summary>Builds the <c>Messaging</c> section a host would receive from the AppHost or a chart.</summary>
internal static class WolverineTestConfiguration
{
    public static Dictionary<string, string?> Messaging(
        WolverineBrokerTarget broker,
        MessagingTestKeys keys,
        string principal,
        IReadOnlyList<string> trustedPrincipals)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [$"{MessagingOptions.SectionName}:Provider"] = "Wolverine",
            [$"{MessagingOptions.SectionName}:ServiceName"] = principal,
            [$"{MessagingOptions.SectionName}:ProducerPrincipal"] = principal,
            [$"{MessagingOptions.SectionName}:RabbitMq:HostName"] = broker.HostName,
            [$"{MessagingOptions.SectionName}:RabbitMq:Port"] = broker.Port.ToString(CultureInfo.InvariantCulture),
            [$"{MessagingOptions.SectionName}:RabbitMq:VirtualHost"] = broker.VirtualHost,
            [$"{MessagingOptions.SectionName}:RabbitMq:UserName"] = principal,
            [$"{MessagingOptions.SectionName}:RabbitMq:Password"] = broker.Password,
            [$"{MessagingOptions.SectionName}:RabbitMq:UseTls"] = "false",
            [$"{MessagingOptions.SectionName}:RabbitMq:AllowInsecureTransport"] = "true",
            [$"{MessagingOptions.SectionName}:RabbitMq:PrefetchCount"] = "8",
            [$"{MessagingOptions.SectionName}:RabbitMq:ConcurrentMessageLimit"] = "4",
            [$"{MessagingOptions.SectionName}:Trust:ActiveKeyId"] = keys.KeyIdFor(principal),
            [$"{MessagingOptions.SectionName}:Trust:PrivateKeyPath"] = keys.PrivateKeyPathFor(principal),

            // Fast but bounded outbox retries, with enough attempts that a failing row is observed before it parks.
            [$"{MessagingOptions.SectionName}:Outbox:PollInterval"] = "00:00:00.200",
            [$"{MessagingOptions.SectionName}:Outbox:DispatchTimeout"] = "00:00:10",
            [$"{MessagingOptions.SectionName}:Outbox:LeaseDuration"] = "00:01:00",
            [$"{MessagingOptions.SectionName}:Outbox:RetryBaseDelay"] = "00:00:00.200",
            [$"{MessagingOptions.SectionName}:Outbox:RetryMaxDelay"] = "00:00:00.500",
            [$"{MessagingOptions.SectionName}:Outbox:MaxAttempts"] = "100"
        };

        for (var index = 0; index < trustedPrincipals.Count; index++)
        {
            var trusted = trustedPrincipals[index];
            var prefix = $"{MessagingOptions.SectionName}:Trust:PublicKeys:{index}";
            settings[$"{prefix}:KeyId"] = keys.KeyIdFor(trusted);
            settings[$"{prefix}:Actor"] = trusted;
            settings[$"{prefix}:PublicKeyPath"] = keys.PublicKeyPathFor(trusted);
            settings[$"{prefix}:Revoked"] = "false";
        }

        return settings;
    }

    public static IConfiguration Build(IReadOnlyDictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
}

/// <summary>
/// Test-only ECDSA P-256 signing material (the only algorithm the TenantWright signer accepts), one pair per principal,
/// written read-only to a temporary directory and removed on disposal.
/// </summary>
internal sealed class MessagingTestKeys : IDisposable
{
    private readonly string _run = Guid.NewGuid().ToString("N")[..12];
    private readonly string _root;
    private readonly Dictionary<string, (string PrivatePath, string PublicPath)> _paths = new(StringComparer.Ordinal);

    public MessagingTestKeys(params string[] principals)
    {
        _root = Path.Combine(Path.GetTempPath(), $"tenantwright-wolverine-keys-{_run}");
        Directory.CreateDirectory(_root);
        foreach (var principal in principals)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var privatePath = Path.Combine(_root, $"{principal}.private.pem");
            var publicPath = Path.Combine(_root, $"{principal}.public.pem");
            File.WriteAllText(privatePath, key.ExportPkcs8PrivateKeyPem());
            File.WriteAllText(publicPath, key.ExportSubjectPublicKeyInfoPem());

            // The validator requires read-only regular files, like the mode-0400 files production mounts.
            File.SetAttributes(privatePath, FileAttributes.ReadOnly);
            File.SetAttributes(publicPath, FileAttributes.ReadOnly);
            _paths[principal] = (privatePath, publicPath);
        }
    }

    /// <summary>Key IDs are unique per key set, so two sets can never be confused for one another.</summary>
    public string KeyIdFor(string principal) => $"{principal}-test-{_run}";

    public string PrivateKeyPathFor(string principal) => _paths[principal].PrivatePath;

    public string PublicKeyPathFor(string principal) => _paths[principal].PublicPath;

    public void Dispose()
    {
        try
        {
            foreach (var (privatePath, publicPath) in _paths.Values)
            {
                if (File.Exists(privatePath)) File.SetAttributes(privatePath, FileAttributes.Normal);
                if (File.Exists(publicPath)) File.SetAttributes(publicPath, FileAttributes.Normal);
            }
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary key directory is harmless; failing disposal would mask the real test result.
        }
    }
}

/// <summary>An async-local ambient tenant, like the production <c>CurrentTenant</c>.</summary>
internal sealed class TestAmbientTenant : ICurrentTenant
{
    private readonly AsyncLocal<Guid?> _tenantId = new();

    public bool IsAvailable => Id.HasValue;
    public Guid? Id => _tenantId.Value;
    public string? Name => null;

    public IDisposable Change(Guid? id, string? name = null)
    {
        var previous = _tenantId.Value;
        _tenantId.Value = id;
        return new Restore(() => _tenantId.Value = previous);
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        private Action? _restore = restore;
        public void Dispose() => Interlocked.Exchange(ref _restore, null)?.Invoke();
    }
}

internal sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = environmentName;
    public string ApplicationName { get; set; } = "Shared.EventBus.Wolverine.Tests";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

/// <summary>Captures every log entry a host writes, so a test can prove a normal start logs no warning or error.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<CapturedLog> _entries = new();

    public IReadOnlyList<CapturedLog> Entries => _entries.ToArray();

    public IReadOnlyList<CapturedLog> AtOrAbove(LogLevel level) =>
        _entries.Where(entry => entry.Level >= level).ToArray();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(string category, ConcurrentQueue<CapturedLog> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
                entries.Enqueue(new CapturedLog(category, logLevel, formatter(state, exception), exception?.GetType().Name));
        }
    }
}

internal sealed record CapturedLog(string Category, LogLevel Level, string Message, string? ExceptionType)
{
    public override string ToString() => $"[{Level}] {Category}: {Message} {ExceptionType}";
}
