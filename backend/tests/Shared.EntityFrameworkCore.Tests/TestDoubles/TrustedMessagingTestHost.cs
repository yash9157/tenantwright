using System.Security.Cryptography;
using MassTransit;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shared.AuditLogs;
using Shared.Autofac;
using Shared.Caching;
using Shared.Caching.Abstractions;
using Shared.Data.ConnectionStrings;
using Shared.Permissions.Caching;
using Shared.Threading;
using Shared.Uow.Abstractions;
using Shared.EntityFrameworkCore.DependencyInjection;
using Shared.EventBus.Hosting;
using Shared.EntityFrameworkCore.Messaging;
using Shared.EventBus.Messaging;
using Shared.Modularity.Context;
using Shared.Modularity.Extensions;
using Shared.Modularity.Modules;
using Shared.MultiTenancy;
using Shared.Permissions;
using Shared.Uow;
using Xunit;

namespace Shared.EntityFrameworkCore.Tests.TestDoubles;

/// <summary>
/// Serialises the trusted-messaging integration tests against every other collection in this assembly.
/// </summary>
/// <remarks>
/// <see cref="DevelopmentEnvironmentScope"/> mutates the process-wide <c>ASPNETCORE_ENVIRONMENT</c> variable, because
/// <c>CachingEnvironmentGuard</c> reads it directly rather than through <c>IHostEnvironment</c>. Disabling
/// parallelisation is what makes that mutation safe: no other collection runs while it is in effect.
/// </remarks>
[CollectionDefinition(TrustedMessagingCollection.Name, DisableParallelization = true)]
public sealed class TrustedMessagingCollection
{
    public const string Name = "trusted-messaging-integration";
}

/// <summary>
/// Temporarily presents the process as Development so <c>CachingModule</c> takes its in-memory provider path, and
/// restores the previous value on disposal.
/// </summary>
/// <remarks>
/// This test verifies transactional and trust behavior, not distributed-cache semantics, so requiring Valkey would add
/// a dependency that proves nothing here. The guard is production behavior and is deliberately not modified; the test
/// satisfies it instead of weakening it.
/// </remarks>
internal sealed class DevelopmentEnvironmentScope : IDisposable
{
    private const string Variable = "ASPNETCORE_ENVIRONMENT";
    private readonly string? _previous;

    public DevelopmentEnvironmentScope()
    {
        _previous = Environment.GetEnvironmentVariable(Variable);
        Environment.SetEnvironmentVariable(Variable, "Development");
    }

    public void Dispose() => Environment.SetEnvironmentVariable(Variable, _previous);
}

/// <summary>
/// The context used by the trusted-messaging suite: business rows plus the neutral outbox, inbox and replay tables,
/// mapped through the same extension every service DbContext uses.
/// </summary>
/// <remarks>
/// It derives from <see cref="SharedDbContext{TDbContext}"/> because <c>IDbContextProvider&lt;TDbContext&gt;</c> is
/// constrained to <c>IEfCoreDbContext</c>, which only the shared base implements. A plain <c>DbContext</c> cannot be obtained from the real provider, so the shared base is required for
/// the unit-of-work path this suite exists to exercise.
/// </remarks>
internal sealed class TrustedMessagingDbContext(DbContextOptions<TrustedMessagingDbContext> options)
    : SharedDbContext<TrustedMessagingDbContext>(options)
{
    public DbSet<ProducerWrite> ProducerWrites => Set<ProducerWrite>();
    public DbSet<ConsumerEffect> ConsumerEffects => Set<ConsumerEffect>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<ProducerWrite>(entity => entity.HasKey(write => write.MessageId));
        modelBuilder.Entity<ConsumerEffect>(entity => entity.HasKey(effect => effect.MessageId));
        modelBuilder.AddTenantWrightMessagingEntities();
    }
}

/// <summary>
/// The startup module for a trusted-messaging test host.
/// </summary>
/// <remarks>
/// Depends on the real modules a service host depends on, so conventional registration supplies
/// <c>UnitOfWorkExecutor</c>, <c>UnitOfWorkDbContextProvider&lt;&gt;</c> and the
/// <c>PermissionProviderStampHybridCache</c> chain. Nothing here hand-registers those types: the point of composing the
/// module graph is that the test uses the same wiring a service does rather than an approximation of it.
///
/// The owner principal comes from <c>Messaging:ServiceName</c> so one module serves both the producer and
/// the consumer host.
/// </remarks>
[DependsOn(typeof(SharedEntityFrameworkCoreModule))]
[DependsOn(typeof(UowModule))]
[DependsOn(typeof(CachingModule))]
[DependsOn(typeof(MultiTenancyModule))]
[DependsOn(typeof(SharedPermissionsModule))]
[DependsOn(typeof(SharedAuditLogsModule))]
public sealed class TrustedMessagingTestModule : BaseModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        var connectionString = context.Configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("The trusted messaging test host requires ConnectionStrings:Default.");
        var ownerPrincipal = context.Configuration[$"{MessagingOptions.SectionName}:ServiceName"]
            ?? throw new InvalidOperationException("The trusted messaging test host requires a messaging service name.");

        context.Services.AddDbContext<TrustedMessagingDbContext>(options =>
        {
            options.UseNpgsql(connectionString);
            options.AddSharedDbContextOptionsExtension();
        });

        var repositoryOptions = new SharedDbContextRegistrationOptions(
            typeof(TrustedMessagingDbContext), context.Services);
        repositoryOptions.AddDefaultRepositories(includeAllEntities: true);
        new EfCoreRepositoryRegistrar(repositoryOptions).AddRepositories();

        context.Services.AddTenantWrightMessagingHost<TrustedMessagingDbContext>(
            context.Configuration,
            ownerPrincipal);

        // Observe the real permission-cache effect at the handler's write boundary, in test composition only. The
        // production consumer, handler and cache operation all still run; the subclass records the key and calls base.
        //
        // Decorating Microsoft's IDistributedCache was tried first and records nothing here: SharedHybridCache writes
        // through HybridCache, whose L2 the in-memory Development path does not route to the framework distributed
        // cache, so no write ever reaches it.
        // The resolver allows host fallback only for a tenant the tenant store positively knows as active. These hosts
        // have no Tenant service, so every tenant the proofs use is an active tenant of the shared service database.
        context.Services.Replace(ServiceDescriptor.Singleton<ITenantStore, ActiveSharedDatabaseTenantStore>());

        context.Services.AddSingleton<StampCacheWriteRecorder>();
        context.Services.Replace(
            ServiceDescriptor.Singleton<PermissionProviderStampHybridCache, RecordingPermissionProviderStampCache>());
    }
}

/// <summary>
/// Reports every tenant as active with no dedicated connection string, so the production resolver takes its
/// configured shared-database fallback. Inactive-tenant fail-closed behavior is covered by the resolver's own tests.
/// </summary>
internal sealed class ActiveSharedDatabaseTenantStore : ITenantStore
{
    [Obsolete("Use FindAsync(Guid, CancellationToken) for non-blocking tenant resolution.")]
    public TenantConfiguration? Find(Guid id) => Active(id);

    [Obsolete("Use FindAsync(string, CancellationToken) for non-blocking tenant resolution.")]
    public TenantConfiguration? Find(string normalizedName) => null;

    [Obsolete("Use GetListAsync(CancellationToken) for non-blocking tenant resolution.")]
    public IReadOnlyList<TenantConfiguration> GetList() => [];

    public Task<TenantConfiguration?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult<TenantConfiguration?>(Active(id));

    public Task<TenantConfiguration?> FindAsync(string normalizedName, CancellationToken cancellationToken = default) =>
        Task.FromResult<TenantConfiguration?>(null);

    public async Task<IReadOnlyList<TenantConfiguration>?> FindListAsync(CancellationToken cancellationToken = default) =>
        await GetListAsync(cancellationToken);

    public Task<IReadOnlyList<TenantConfiguration>> GetListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TenantConfiguration>>([]);

    private static TenantConfiguration Active(Guid id) => new()
    {
        Id = id,
        Name = $"tenant-{id:N}",
        NormalizedName = $"TENANT-{id:N}",
        IsActive = true
    };
}

/// <summary>
/// The production stamp cache with its write recorded. Overrides nothing but the observation.
/// </summary>
internal sealed class RecordingPermissionProviderStampCache(
    IServiceProvider serviceProvider,
    Microsoft.Extensions.Options.IOptions<SharedHybridCacheOptions> hybridCacheOption,
    Microsoft.Extensions.Caching.Hybrid.HybridCache hybridCache,
    Microsoft.Extensions.Caching.Distributed.IDistributedCache distributedCache,
    ICancellationTokenProvider cancellationTokenProvider,
    IDistributedCacheKeyNormalizer keyNormalizer,
    IServiceScopeFactory serviceScopeFactory,
    IUnitOfWorkManager unitOfWorkManager,
    StampCacheWriteRecorder recorder)
    : PermissionProviderStampHybridCache(
        serviceProvider,
        hybridCacheOption,
        hybridCache,
        distributedCache,
        cancellationTokenProvider,
        keyNormalizer,
        serviceScopeFactory,
        unitOfWorkManager)
{
    public override Task SetAsync(
        string key,
        PermissionProviderStampCacheItem value,
        Microsoft.Extensions.Caching.Hybrid.HybridCacheEntryOptions? options = null,
        bool? hideErrors = null,
        bool considerUow = false,
        CancellationToken token = default)
    {
        recorder.Record(key);
        return base.SetAsync(key, value, options, hideErrors, considerUow, token);
    }
}

/// <summary>
/// Builds and owns one module-based trusted-messaging host.
/// </summary>
internal sealed class TrustedMessagingHost : IAsyncDisposable
{
    private readonly IServiceProvider _provider;
    private readonly List<Microsoft.Extensions.Hosting.IHostedService> _startedServices = [];

    private TrustedMessagingHost(IServiceProvider provider, string principal)
    {
        _provider = provider;
        Principal = principal;
    }

    public string Principal { get; }
    public IServiceProvider Services => _provider;

    /// <summary>
    /// Composes the module graph, builds the provider with scope validation, and runs module initialization.
    /// </summary>
    public static async Task<TrustedMessagingHost> CreateAsync(
        DisposableMessagingInfrastructure infrastructure,
        MessagingTrustKeys keys,
        string principal,
        string connectionName,
        string? postgresConnectionString = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var configuration = BuildConfiguration(
            infrastructure,
            keys,
            principal,
            connectionName,
            postgresConnectionString ?? infrastructure.PostgresConnectionString);
        services.AddSingleton<IConfiguration>(configuration);

        // MessagingOptionsValidator takes IHostEnvironment, which a web host supplies. Reported as Development so the
        // validator applies the same relaxations CachingModule does for this composition.
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(new TrustedMessagingHostEnvironment());

        var application = services.AddApplication<TrustedMessagingTestModule>(configuration);

        // The same provider factory a service host installs through UseSharedAutofac. This is what activates
        // UnitOfWorkInterceptor, without which the [UnitOfWork] attribute on UnitOfWorkExecutor is inert.
        //
        // Microsoft DI's ValidateScopes has no Autofac equivalent; Autofac enforces lifetime rules at resolution
        // instead. The smoke gate resolves every service under test from a child scope, which exercises that
        // enforcement on the real composition rather than on a parallel validation pass.
        var factory = new AutofacServiceProviderFactory();
        var containerBuilder = factory.CreateBuilder(services);
        var provider = factory.CreateServiceProvider(containerBuilder);

        await application.InitializeAsync(new ApplicationBuilder(provider));
        return new TrustedMessagingHost(provider, principal);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_startedServices.Count > 0)
            return;

        try
        {
            foreach (var service in _provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>())
            {
                await service.StartAsync(cancellationToken);
                _startedServices.Add(service);
            }
        }
        catch
        {
            await StopAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// Attaches an observer to this host's existing consume pipeline. Call before <see cref="StartAsync"/> so no
    /// delivery is missed. Adds no queue and changes no topology.
    /// </summary>
    public ConnectHandle ConnectConsumeObserver(IConsumeObserver observer)
        => _provider.GetRequiredService<IBusControl>().ConnectConsumeObserver(observer);

    /// <summary>
    /// Attaches a transport-level receive observer, which is where a rejected or unroutable envelope surfaces.
    /// </summary>
    public ConnectHandle ConnectReceiveObserver(IReceiveObserver observer)
        => _provider.GetRequiredService<IBusControl>().ConnectReceiveObserver(observer);

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        for (var index = _startedServices.Count - 1; index >= 0; index--)
            await _startedServices[index].StopAsync(cancellationToken);

        _startedServices.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync(CancellationToken.None);
        }
        finally
        {
            // The Autofac-backed provider owns the root lifetime scope; disposing it releases the container.
            switch (_provider)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync();
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }
    }

    private static IConfiguration BuildConfiguration(
        DisposableMessagingInfrastructure infrastructure,
        MessagingTrustKeys keys,
        string principal,
        string connectionName,
        string postgresConnectionString)
    {
        if (string.IsNullOrWhiteSpace(infrastructure.ValkeyConfiguration))
        {
            throw new InvalidOperationException(
                "The trusted messaging host requires a disposable Valkey: DistributedLockingModule will not configure "
                + "without one. Start the infrastructure with includeValkey: true.");
        }

        // Unique per run and per principal, so concurrent runs cannot share cache or lock keys.
        var keyPrefix = $"trusted-messaging-test:{principal}:{Guid.NewGuid():N}:";

        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ConnectionStrings:Default"] = postgresConnectionString,

            // TenantConnectionStringResolver fails closed for a tenant-scoped operation unless the tenant has its own
            // connection string or host fallback is allowed. Every TenantWright service appsettings.json sets this to true
            // because they share one database per service, and the trusted delivery proofs publish under an ambient
            // tenant, so the test host must configure it the same way.
            [$"{SharedConnectionStringResolverOptions.SectionName}:AllowHostFallbackForTenant"] = "true",

            // PermissionGrantChangedEventHandler returns without touching the cache when the permission client is
            // disabled, and SharedPermissionsModule's ValidateOnStart never fires here because this host has no generic
            // Host to run it. Without this the consumer runs and silently performs no effect.
            ["Permissions:Enabled"] = "true",

            // Any non-Valkey provider selects the in-memory path inside CachingModule. Cache transport semantics are
            // outside this test, so Valkey is deliberately not used for caching.
            ["Caching:Provider"] = "Memory",
            ["Caching:KeyPrefix"] = keyPrefix,

            // DistributedLockingModule supports only Valkey and refuses to configure without a connection string, so
            // this is the one place the disposable Valkey is used. The key prefix is unique per host composition so two
            // runs cannot contend on the same lock keys.
            ["DistributedLocking:Valkey:Configuration"] = infrastructure.ValkeyConfiguration,
            ["DistributedLocking:KeyPrefix"] = keyPrefix,

            [$"{MessagingOptions.SectionName}:Provider"] = "MassTransit",
            [$"{MessagingOptions.SectionName}:ServiceName"] = principal,
            [$"{MessagingOptions.SectionName}:ProducerPrincipal"] = principal,
            [$"{MessagingOptions.SectionName}:RabbitMq:HostName"] = "127.0.0.1",
            [$"{MessagingOptions.SectionName}:RabbitMq:Port"] =
                infrastructure.RabbitPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [$"{MessagingOptions.SectionName}:RabbitMq:VirtualHost"] = infrastructure.RabbitVirtualHost,

            // Must equal the producer principal exactly; the broker has one user per principal.
            [$"{MessagingOptions.SectionName}:RabbitMq:UserName"] = principal,
            [$"{MessagingOptions.SectionName}:RabbitMq:Password"] = infrastructure.RabbitPassword,
            [$"{MessagingOptions.SectionName}:RabbitMq:UseTls"] = "false",
            [$"{MessagingOptions.SectionName}:RabbitMq:AllowInsecureTransport"] = "true",
            [$"{MessagingOptions.SectionName}:RabbitMq:ConnectionName"] = connectionName,
            [$"{MessagingOptions.SectionName}:RabbitMq:PrefetchCount"] = "8",
            [$"{MessagingOptions.SectionName}:RabbitMq:ConcurrentMessageLimit"] = "4",
            [$"{MessagingOptions.SectionName}:Trust:ActiveKeyId"] = keys.KeyIdFor(principal),
            [$"{MessagingOptions.SectionName}:Trust:PrivateKeyPath"] = keys.PrivateKeyPathFor(principal)
        };

        // Every participating principal is trusted for verification, which is what lets the consumer host validate the
        // producer's origin signature.
        var index = 0;
        foreach (var trusted in keys.Principals)
        {
            var prefix = $"{MessagingOptions.SectionName}:Trust:PublicKeys:{index}";
            settings[$"{prefix}:KeyId"] = keys.KeyIdFor(trusted);
            settings[$"{prefix}:Actor"] = trusted;
            settings[$"{prefix}:PublicKeyPath"] = keys.PublicKeyPathFor(trusted);
            settings[$"{prefix}:Revoked"] = "false";
            index++;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }
}

/// <summary>Minimal <c>IHostEnvironment</c> for a non-web test host.</summary>
internal sealed class TrustedMessagingHostEnvironment : Microsoft.Extensions.Hosting.IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Development";
    public string ApplicationName { get; set; } = "Shared.EntityFrameworkCore.Tests";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

    public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
        new Microsoft.Extensions.FileProviders.NullFileProvider();
}

/// <summary>
/// Test-only ECDSA signing material, one key pair per participating principal, written to a temporary directory and
/// removed on disposal. No production key or secret is copied.
/// </summary>
internal sealed class MessagingTrustKeys : IDisposable
{
    private readonly string _root;
    private readonly Dictionary<string, (string PrivatePath, string PublicPath)> _paths = new(StringComparer.Ordinal);

    public MessagingTrustKeys(params string[] principals)
    {
        Principals = principals;
        _root = Path.Combine(Path.GetTempPath(), $"tenantwright-trust-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        foreach (var principal in principals)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var privatePath = Path.Combine(_root, $"{principal}.private.pem");
            var publicPath = Path.Combine(_root, $"{principal}.public.pem");
            File.WriteAllText(privatePath, key.ExportPkcs8PrivateKeyPem());
            File.WriteAllText(publicPath, key.ExportSubjectPublicKeyInfoPem());

            // MessagingOptionsValidator requires read-only regular non-reparse files, mirroring the mode-0400 files the
            // production init container materializes. Satisfied rather than relaxed.
            File.SetAttributes(privatePath, FileAttributes.ReadOnly);
            File.SetAttributes(publicPath, FileAttributes.ReadOnly);

            _paths[principal] = (privatePath, publicPath);
        }
    }

    public IReadOnlyList<string> Principals { get; }

    public string KeyIdFor(string principal) => $"{principal}-test-key";

    public string PrivateKeyPathFor(string principal) => _paths[principal].PrivatePath;

    public string PublicKeyPathFor(string principal) => _paths[principal].PublicPath;

    public void Dispose()
    {
        try
        {
            // The read-only attribute has to come off before the directory can be removed.
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

/// <summary>
/// Records every distributed-cache write a host performs, so the real permission-cache effect is observable.
/// </summary>
/// <remarks>
/// This is the test-visible effect boundary for the duplicate-suppression proof. The production consumer, handler and
/// cache invalidation all still run unchanged; only the cache write is observed on the way through. A consume observer
/// cannot serve this purpose, because MassTransit's inbox short-circuits before the consumer is invoked and both
/// "suppressed" and "never arrived" look identical from there.
/// </remarks>
internal sealed class StampCacheWriteRecorder
{
    private readonly List<string> _keys = [];

    public void Record(string key)
    {
        lock (_keys)
        {
            _keys.Add(key);
        }
    }

    /// <summary>Writes whose normalized key contains <paramref name="logicalKey"/>.</summary>
    public int CountFor(string logicalKey)
    {
        lock (_keys)
        {
            return _keys.Count(key => key.Contains(logicalKey, StringComparison.Ordinal));
        }
    }

    /// <summary>All recorded keys, for diagnosing a normalizer that transforms rather than prefixes.</summary>
    public string Describe()
    {
        lock (_keys)
        {
            return _keys.Count == 0 ? "<no writes>" : string.Join(" | ", _keys);
        }
    }
}
