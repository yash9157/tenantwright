using System.Collections.Concurrent;
using System.Security.Cryptography;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shared.AuditLogs.Auditing;
using Shared.Data.ConnectionStrings;
using Shared.Data.Filtering;
using Shared.EntityFrameworkCore.Messaging;
using Shared.EntityFrameworkCore.Uow.EntityFrameworkCore;
using Shared.EventBus.Messaging;
using Shared.EventContracts;
using Shared.MultiTenancy;
using Shared.Threading;
using Shared.Uow.Abstractions;
using Shared.Uow.Context;
using Shared.Uow.Implementations;
using Shared.Uow.Options;
using Xunit;

namespace Shared.EntityFrameworkCore.Tests.TestDoubles;

/// <summary>
/// One disposable PostgreSQL container for the neutral outbox/inbox suite. Each test creates its own databases, so
/// tests never observe each other's rows.
/// </summary>
public sealed class NeutralMessagingPostgresFixture : IAsyncLifetime
{
    private const string User = "tenantwright";
    private const string Database = "tenantwright_neutral";
    private readonly string _password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    private IContainer? _container;

    public string AdminConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        _container = new ContainerBuilder(MessagingIntegrationDefaults.PostgresImage)
            .WithName($"{MessagingIntegrationDefaults.ContainerPrefix}-neutral-pg-{Guid.NewGuid():N}")
            .WithEnvironment("POSTGRES_USER", User)
            .WithEnvironment("POSTGRES_PASSWORD", _password)
            .WithEnvironment("POSTGRES_DB", Database)
            .WithPortBinding(5432, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilMessageIsLogged("PostgreSQL init process complete; ready for start up"))
            .Build();
        await _container.StartAsync();

        AdminConnectionString = new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Port = _container.GetMappedPublicPort(5432),
            Database = Database,
            Username = User,
            Password = _password,
            Pooling = false
        }.ConnectionString;

        // The image restarts the server once after initialization; wait for the final instance to accept queries.
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            try
            {
                await using var connection = new NpgsqlConnection(AdminConnectionString);
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand("SELECT 1", connection);
                await command.ExecuteScalarAsync();
                return;
            }
            catch (Exception exception) when (exception is NpgsqlException or IOException && DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500));
            }
        }
    }

    /// <summary>Creates an isolated database with the neutral messaging schema and returns its connection string.</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var connectionString = await CreateEmptyDatabaseAsync();
        await using var dbContext = new NeutralMessagingDbContext(
            new DbContextOptionsBuilder<NeutralMessagingDbContext>().UseNpgsql(connectionString).Options);
        await dbContext.Database.EnsureCreatedAsync();
        return connectionString;
    }

    /// <summary>Creates an isolated, empty database (no schema) and returns its connection string.</summary>
    public async Task<string> CreateEmptyDatabaseAsync()
    {
        var name = $"neutral_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(AdminConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
            await command.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(AdminConnectionString) { Database = name, Pooling = true }
            .ConnectionString;
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        if (_container is not null)
            await _container.DisposeAsync();
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class NeutralMessagingPostgresSuite : ICollectionFixture<NeutralMessagingPostgresFixture>
{
    public const string Name = "neutral-messaging-postgres";
}

/// <summary>
/// The suite's service DbContext: business rows plus the neutral messaging tables, as every service maps them. It is
/// named like a dedicatable service database so tenant-database fan-out applies to it.
/// </summary>
[ConnectionStringName(TenantWrightDatabaseCatalog.Notifications)]
internal sealed class NeutralMessagingDbContext(DbContextOptions<NeutralMessagingDbContext> options)
    : SharedDbContext<NeutralMessagingDbContext>(options)
{
    public DbSet<ProducerWrite> ProducerWrites => Set<ProducerWrite>();
    public DbSet<ConsumerEffect> ConsumerEffects => Set<ConsumerEffect>();
    public DbSet<MessagingOutboxMessage> OutboxMessages => Set<MessagingOutboxMessage>();
    public DbSet<MessagingInboxRecord> InboxRecords => Set<MessagingInboxRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<ProducerWrite>(entity => entity.HasKey(write => write.MessageId));
        modelBuilder.Entity<ConsumerEffect>(entity => entity.HasKey(effect => effect.MessageId));
        modelBuilder.AddTenantWrightMessagingEntities();
    }
}

/// <summary>
/// Records every dispatch and can fail on demand. When a consumer host is attached it loops each dispatched message
/// into that host's real inbound pipeline, under the producer's broker identity, as a broker would deliver it.
/// </summary>
internal sealed class LoopbackTransport(string brokerActor) : ITenantWrightMessagingTransport
{
    private static readonly System.Reflection.MethodInfo ProcessMethod =
        typeof(TenantWrightInboundMessagePipeline).GetMethod(nameof(TenantWrightInboundMessagePipeline.ProcessAsync))!;

    public ConcurrentDictionary<Guid, int> Dispatches { get; } = new();
    public Func<TenantWrightTransportMessage, Exception?> Fault { get; set; } = _ => null;
    public TimeSpan Latency { get; set; } = TimeSpan.Zero;
    public NeutralMessagingHost? Consumer { get; set; }
    public string? ConsumerEndpoint { get; set; }

    public async Task DispatchAsync(TenantWrightTransportMessage message, CancellationToken cancellationToken)
    {
        if (Latency > TimeSpan.Zero)
            await Task.Delay(Latency, cancellationToken);
        if (Fault(message) is { } exception)
            throw exception;

        if (Consumer is not null)
        {
            var catalog = Consumer.Services.GetRequiredService<TenantWrightMessageCatalog>();
            var route = catalog.GetRouteByIdentity(message.ContractIdentity);
            var contract = catalog.DeserializeCanonical(message.Payload.Span, route.ContractType);
            var inbound = new TenantWrightInboundMessage(
                ConsumerEndpoint!,
                message.MessageId,
                contract,
                message.Headers.ToDictionary(header => header.Key, string? (header) => header.Value, StringComparer.Ordinal),
                brokerActor,
                1,
                NoTenantWrightTransportEvidence.Instance);
            await using var scope = Consumer.Services.CreateAsyncScope();
            var pipeline = scope.ServiceProvider.GetRequiredService<TenantWrightInboundMessagePipeline>();
            await (Task)ProcessMethod.MakeGenericMethod(route.ContractType).Invoke(pipeline, [inbound, cancellationToken])!;
        }

        Dispatches.AddOrUpdate(message.MessageId, 1, (_, count) => count + 1);
    }
}

/// <summary>
/// The suite's test catalog: <c>tenantwright-permissions</c> publishes tenant permission changes that
/// <c>tenantwright-identity</c> consumes on one endpoint; the consumer's handler emits a tenant feature change.
/// </summary>
internal static class NeutralMessagingTopology
{
    public const string Producer = TenantWrightMessagingTopology.PermissionsPrincipal;
    public const string Consumer = TenantWrightMessagingTopology.IdentityPrincipal;
    public const string Endpoint = "tenantwright-identity-neutral-suite-v1";

    public static TenantWrightMessageCatalog CreateCatalog(bool consumes) => new(
        [
            new TenantWrightContractRoute(typeof(PermissionGrantsChangedTenantEto), TenantWrightMessageRouteKind.Event, null,
                new HashSet<string>(StringComparer.Ordinal) { Producer }),
            new TenantWrightContractRoute(typeof(FeatureValuesChangedTenantEto), TenantWrightMessageRouteKind.Event, null,
                new HashSet<string>(StringComparer.Ordinal) { Consumer })
        ],
        [new TenantWrightEndpointRoute(Endpoint, Consumer, [typeof(PermissionGrantsChangedTenantEto)])],
        consumes ? [new TenantWrightReceiveEndpoint(Endpoint, Consumer, [typeof(PermissionGrantsChangedTenantEto)])] : []);
}

/// <summary>Observes the consumer handler: effects written in its transaction, its tenant, and how often it ran.</summary>
internal sealed class ConsumerProbe
{
    private int _invocations;

    public int Invocations => Volatile.Read(ref _invocations);
    public ConcurrentBag<Guid?> ObservedTenants { get; } = [];
    public TimeSpan HoldTransactionFor { get; set; } = TimeSpan.Zero;
    public Func<PermissionGrantsChangedTenantEto, bool> FailWhen { get; set; } = _ => false;

    public void RecordInvocation(Guid? tenant)
    {
        Interlocked.Increment(ref _invocations);
        ObservedTenants.Add(tenant);
    }
}

/// <summary>
/// The production-shaped consumer handler: it writes a business effect and emits a follow-up event, both inside the
/// inbox transaction the neutral pipeline opened.
/// </summary>
internal sealed class ConsumerEffectHandler(
    IDbContextProvider<NeutralMessagingDbContext> dbContextProvider,
    ITenantWrightMessageSender sender,
    ICurrentTenant currentTenant,
    ConsumerProbe probe) : ITenantWrightMessageHandler<PermissionGrantsChangedTenantEto>
{
    public async Task HandleAsync(PermissionGrantsChangedTenantEto message, CancellationToken cancellationToken)
    {
        probe.RecordInvocation(currentTenant.Id);
        var dbContext = await dbContextProvider.GetDbContextAsync();
        dbContext.ConsumerEffects.Add(new ConsumerEffect(Guid.Parse(message.ProviderKey)));
        await sender.PublishEventAsync(new FeatureValuesChangedTenantEto
        {
            TenantId = message.TenantId,
            OccurredAtUtc = message.OccurredAtUtc,
            FeatureNames = [message.ProviderKey]
        }, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        if (probe.HoldTransactionFor > TimeSpan.Zero)
            await Task.Delay(probe.HoldTransactionFor, cancellationToken);
        if (probe.FailWhen(message))
            throw new InvalidOperationException("consumer handler failed");
    }
}

/// <summary>
/// One service host composed like production for the neutral layer: real unit of work, real
/// <see cref="UnitOfWorkDbContextProvider{TDbContext}"/>, real signing keys, the neutral outbox, dispatcher, inbox and
/// pipeline, and a <see cref="LoopbackTransport"/> in place of a broker.
/// </summary>
internal sealed class NeutralMessagingHost : IAsyncDisposable
{
    private NeutralMessagingHost(ServiceProvider services, string principal)
    {
        Services = services;
        Principal = principal;
    }

    public ServiceProvider Services { get; }
    public string Principal { get; }
    public LoopbackTransport Transport => (LoopbackTransport)Services.GetRequiredService<ITenantWrightMessagingTransport>();
    public TestAmbientTenant CurrentTenant => (TestAmbientTenant)Services.GetRequiredService<ICurrentTenant>();

    public TenantWrightOutboxDispatcher<NeutralMessagingDbContext> Dispatcher =>
        Services.GetServices<IHostedService>().OfType<TenantWrightOutboxDispatcher<NeutralMessagingDbContext>>().Single();

    public TenantWrightInboxRetentionService<NeutralMessagingDbContext> InboxRetention =>
        Services.GetServices<IHostedService>().OfType<TenantWrightInboxRetentionService<NeutralMessagingDbContext>>().Single();

    public static NeutralMessagingHost Create(
        string connectionString,
        MessagingTrustKeys keys,
        string principal,
        bool consumes,
        IReadOnlyDictionary<string, string?>? outboxSettings = null,
        ConsumerProbe? probe = null,
        LoopbackTransport? transport = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            // Any real provider turns on the neutral connection, trust and replay validation. No MassTransit service is
            // registered: the suite composes the neutral layer over a loopback transport.
            [$"{MessagingOptions.SectionName}:Provider"] = "MassTransit",
            [$"{MessagingOptions.SectionName}:ServiceName"] = principal,
            [$"{MessagingOptions.SectionName}:ProducerPrincipal"] = principal,
            [$"{MessagingOptions.SectionName}:RabbitMq:HostName"] = "127.0.0.1",
            [$"{MessagingOptions.SectionName}:RabbitMq:Port"] = "5672",
            [$"{MessagingOptions.SectionName}:RabbitMq:VirtualHost"] = "tenantwright-neutral-suite",
            [$"{MessagingOptions.SectionName}:RabbitMq:UserName"] = principal,
            [$"{MessagingOptions.SectionName}:RabbitMq:Password"] = "unused-no-broker-in-this-suite",
            [$"{MessagingOptions.SectionName}:RabbitMq:UseTls"] = "false",
            [$"{MessagingOptions.SectionName}:RabbitMq:AllowInsecureTransport"] = "true",
            [$"{MessagingOptions.SectionName}:Trust:ActiveKeyId"] = keys.KeyIdFor(principal),
            [$"{MessagingOptions.SectionName}:Trust:PrivateKeyPath"] = keys.PrivateKeyPathFor(principal),
            [$"{TenantWrightOutboxOptions.SectionName}:PollInterval"] = "00:00:00.200",
            [$"{TenantWrightOutboxOptions.SectionName}:RetryBaseDelay"] = "00:00:00.010",
            [$"{TenantWrightOutboxOptions.SectionName}:RetryMaxDelay"] = "00:00:00.050"
        };
        var index = 0;
        foreach (var trusted in keys.Principals)
        {
            var prefix = $"{MessagingOptions.SectionName}:Trust:PublicKeys:{index++}";
            settings[$"{prefix}:KeyId"] = keys.KeyIdFor(trusted);
            settings[$"{prefix}:Actor"] = trusted;
            settings[$"{prefix}:PublicKeyPath"] = keys.PublicKeyPathFor(trusted);
        }
        foreach (var setting in outboxSettings ?? new Dictionary<string, string?>())
            settings[setting.Key] = setting.Value;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IHostEnvironment>(new TrustedMessagingHostEnvironment());

        // Unit of work and the shared DbContext dependencies, as Shared.Uow and Shared.EntityFrameworkCore provide them.
        services.Configure<UnitOfWorkDefaultOptions>(_ => { });
        services.Configure<SharedDbContextOptions>(_ => { });
        services.AddSingleton<IAmbientUnitOfWork, AmbientUnitOfWork>();
        services.AddSingleton<IUnitOfWorkManager, UnitOfWorkManager>();
        services.AddTransient<IUnitOfWork, UnitOfWork>();
        services.AddSingleton<IConnectionStringResolver>(new FixedConnectionStringResolver(connectionString));
        services.AddSingleton<ITenantStore, ActiveSharedDatabaseTenantStore>();
        services.AddSingleton<ICancellationTokenProvider>(NoCancellationTokenProvider.Instance);
        services.AddSingleton<ICurrentTenant, TestAmbientTenant>();
        services.AddSingleton<IDataFilter, EnabledDataFilter>();
        services.AddSingleton<IAuditPropertySetter, NoAuditPropertySetter>();
        services.AddSingleton<IAuditingManager, NoAuditingManager>();
        services.AddSingleton<IEfCoreDbContextTypeProvider, IdentityDbContextTypeProvider>();
        services.AddDbContext<NeutralMessagingDbContext>(
            options => options.UseNpgsql(connectionString),
            ServiceLifetime.Transient,
            ServiceLifetime.Singleton);
        services.AddTransient(typeof(IDbContextProvider<>), typeof(UnitOfWorkDbContextProvider<>));

        // The neutral messaging layer exactly as AddTenantWrightMessagingHost composes it, minus the provider.
        services.AddTenantWrightMessagingCore(configuration, NeutralMessagingTopology.CreateCatalog(consumes));
        services.AddTenantWrightMessagingPersistence<NeutralMessagingDbContext>(configuration);
        services.AddSingleton<ITenantWrightMessagingTransport>(transport ?? new LoopbackTransport(principal));
        services.AddSingleton(probe ?? new ConsumerProbe());
        services.AddScoped<ITenantWrightMessageHandler<PermissionGrantsChangedTenantEto>, ConsumerEffectHandler>();
        configureServices?.Invoke(services);

        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        return new NeutralMessagingHost(provider, principal);
    }

    /// <summary>Runs <paramref name="work"/> inside a new transactional unit of work and completes it.</summary>
    public async Task InUnitOfWorkAsync(
        Func<IServiceProvider, NeutralMessagingDbContext, Task> work,
        bool complete = true,
        Guid? tenantId = null)
    {
        using var tenant = CurrentTenant.Change(tenantId);
        await using var scope = Services.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        using var unitOfWork = manager.Begin(new UnitOfWorkOptions { IsTransactional = true }, requiresNew: true);
        var dbContext = await scope.ServiceProvider
            .GetRequiredService<IDbContextProvider<NeutralMessagingDbContext>>()
            .GetDbContextAsync();
        await work(scope.ServiceProvider, dbContext);
        if (complete)
            await unitOfWork.CompleteAsync();
    }

    /// <summary>Publishes one tenant permission change through the production sender and returns its correlation key.</summary>
    public static Task PublishAsync(IServiceProvider services, Guid tenantId, Guid key, CancellationToken cancellationToken = default) =>
        services.GetRequiredService<ITenantWrightMessageSender>().PublishEventAsync(new PermissionGrantsChangedTenantEto
        {
            TenantId = tenantId,
            ProviderName = "neutral-suite",
            ProviderKey = key.ToString("D"),
            Stamp = 1,
            OccurredAtUtc = DateTimeOffset.UtcNow
        }, cancellationToken);

    /// <summary>A fresh context on its own connection, outside any unit of work, that sees only committed rows.</summary>
    public NeutralMessagingDbContext ReadContext() => Services.GetRequiredService<NeutralMessagingDbContext>();

    public async Task<List<MessagingOutboxMessage>> OutboxAsync()
    {
        await using var dbContext = ReadContext();
        return await dbContext.OutboxMessages.AsNoTracking().OrderBy(message => message.CreatedAtUtc).ToListAsync();
    }

    /// <summary>Claims and dispatches until no due row remains, or the deadline passes.</summary>
    public async Task DrainAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await Dispatcher.DispatchBatchAsync(CancellationToken.None) == 0)
                return;
        }
    }

    public async ValueTask DisposeAsync() => await Services.DisposeAsync();

    private sealed class FixedConnectionStringResolver(string connectionString) : IConnectionStringResolver
    {
        public Task<string> ResolveAsync(string? connectionStringName = null) => Task.FromResult(connectionString);
    }

    private sealed class NoCancellationTokenProvider : ICancellationTokenProvider
    {
        public static NoCancellationTokenProvider Instance { get; } = new();
        public CancellationToken Token => CancellationToken.None;
    }

    private sealed class IdentityDbContextTypeProvider : IEfCoreDbContextTypeProvider
    {
        public Type GetDbContextType(Type dbContextType) => dbContextType;
    }

    private sealed class EnabledDataFilter : IDataFilter
    {
        public IDisposable Enable<TFilter>() where TFilter : class => new NoScope();
        public IDisposable Disable<TFilter>() where TFilter : class => new NoScope();
        public bool IsEnabled<TFilter>() where TFilter : class => true;
    }

    private sealed class NoAuditPropertySetter : IAuditPropertySetter
    {
        public void SetCreationProperties(object entity) { }
        public void SetModificationProperties(object entity) { }
        public void SetDeletionProperties(object entity) { }
        public void IncrementEntityVersionProperty(object entity) { }
    }

    private sealed class NoAuditingManager : IAuditingManager
    {
        public IAuditLogScope? Current => null;
        public IAuditLogSaveHandle BeginScope() => throw new NotSupportedException("Auditing is outside this suite.");
    }

    private sealed class NoScope : IDisposable
    {
        public void Dispose() { }
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
