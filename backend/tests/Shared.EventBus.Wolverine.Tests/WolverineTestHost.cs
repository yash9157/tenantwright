using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shared.AuditLogs.Auditing;
using Shared.Data.ConnectionStrings;
using Shared.Data.Filtering;
using Shared.EntityFrameworkCore;
using Shared.EntityFrameworkCore.Messaging;
using Shared.EntityFrameworkCore.Uow.EntityFrameworkCore;
using Shared.EventBus.Hosting;
using Shared.EventBus.Messaging;
using Shared.EventContracts;
using Shared.MultiTenancy;
using Shared.Threading;
using Shared.Uow.Abstractions;
using Shared.Uow.Context;
using Shared.Uow.Implementations;
using Shared.Uow.Options;

namespace Shared.EventBus.Wolverine.Tests;

/// <summary>A business effect a consumer handler writes inside its inbox transaction.</summary>
internal sealed class ConsumerEffect(Guid id, string key)
{
    public Guid Id { get; private set; } = id;
    public string Key { get; private set; } = key;
}

/// <summary>The test service DbContext: business rows plus the neutral messaging tables, as every service maps them.</summary>
internal sealed class WolverineTestDbContext(DbContextOptions<WolverineTestDbContext> options)
    : SharedDbContext<WolverineTestDbContext>(options)
{
    public DbSet<ConsumerEffect> ConsumerEffects => Set<ConsumerEffect>();
    public DbSet<MessagingOutboxMessage> OutboxMessages => Set<MessagingOutboxMessage>();
    public DbSet<MessagingInboxRecord> InboxRecords => Set<MessagingInboxRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<ConsumerEffect>(entity => entity.HasKey(effect => effect.Id));
        modelBuilder.AddTenantWrightMessagingEntities();
    }
}

/// <summary>
/// Observes the consumer handlers: how often each business key was handled (failed attempts included), under which
/// tenant, and an on-demand number of transient failures to throw before succeeding.
/// </summary>
internal sealed class DeliveryProbe
{
    private readonly ConcurrentDictionary<string, int> _attempts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _failuresRemaining = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Guid?> _tenants = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _markedTransient = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<DateTimeOffset>> _attemptTimes = new(StringComparer.Ordinal);

    /// <summary>
    /// Fails the next <paramref name="times"/> attempts; <paramref name="markedTransient"/> throws a failure that
    /// implements <see cref="ITenantWrightTransientMessageFailure"/>, as a not-yet-converged handler does.
    /// </summary>
    public void FailTransiently(string key, int times, bool markedTransient = false)
    {
        _markedTransient[key] = markedTransient;
        _failuresRemaining[key] = times;
    }

    public IReadOnlyList<DateTimeOffset> AttemptTimesFor(string key) =>
        _attemptTimes.TryGetValue(key, out var times) ? [.. times] : [];

    public int AttemptsFor(string key) => _attempts.GetValueOrDefault(key);

    public Guid? TenantFor(string key) => _tenants.GetValueOrDefault(key);

    public void Record(string key, Guid? tenant)
    {
        _attempts.AddOrUpdate(key, 1, (_, count) => count + 1);
        _attemptTimes.GetOrAdd(key, _ => new ConcurrentQueue<DateTimeOffset>()).Enqueue(DateTimeOffset.UtcNow);
        _tenants[key] = tenant;
        if (_failuresRemaining.TryGetValue(key, out var remaining) && remaining > 0 &&
            _failuresRemaining.TryUpdate(key, remaining - 1, remaining))
        {
            if (_markedTransient.GetValueOrDefault(key))
                throw new NotConvergedYetException();
            throw new InvalidOperationException("Transient handler failure.");
        }
    }

    /// <summary>A handler failure that clears on its own, marked for the provider's longer transient schedule.</summary>
    private sealed class NotConvergedYetException()
        : InvalidOperationException("The handler's dependency has not converged yet."), ITenantWrightTransientMessageFailure;

    public async Task<bool> WaitForAttemptsAsync(string key, int expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (AttemptsFor(key) >= expected)
                return true;
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        return AttemptsFor(key) >= expected;
    }
}

/// <summary>Handles the tenant command: records the ambient tenant and writes one effect in the inbox transaction.</summary>
internal sealed class TenantCommandHandler(
    IDbContextProvider<WolverineTestDbContext> dbContextProvider,
    ICurrentTenant currentTenant,
    DeliveryProbe probe) : ITenantWrightMessageHandler<NotificationSendRequestedTenantEto>
{
    public async Task HandleAsync(NotificationSendRequestedTenantEto message, CancellationToken cancellationToken)
    {
        var key = message.RequestId.ToString("D");
        probe.Record(key, currentTenant.Id);
        var dbContext = await dbContextProvider.GetDbContextAsync();
        dbContext.ConsumerEffects.Add(new ConsumerEffect(Guid.NewGuid(), key));
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>Handles the tenant event on both event endpoints, failing transiently when the probe asks it to.</summary>
internal sealed class GrantsChangedHandler(
    IDbContextProvider<WolverineTestDbContext> dbContextProvider,
    ICurrentTenant currentTenant,
    DeliveryProbe probe) : ITenantWrightMessageHandler<PermissionGrantsChangedTenantEto>
{
    public async Task HandleAsync(PermissionGrantsChangedTenantEto message, CancellationToken cancellationToken)
    {
        probe.Record(message.ProviderKey, currentTenant.Id);
        var dbContext = await dbContextProvider.GetDbContextAsync();
        dbContext.ConsumerEffects.Add(new ConsumerEffect(Guid.NewGuid(), message.ProviderKey));
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>
/// One in-process service host composed like production: a real generic host running the Wolverine runtime and the
/// outbox dispatcher, real unit of work and PostgreSQL persistence, real signing keys, the neutral core, the Wolverine
/// provider and the neutral outbox/inbox.
/// </summary>
internal sealed class WolverineTestHost : IAsyncDisposable
{
    private WolverineTestHost(IHost host, string principal, CapturingLoggerProvider logs, DeliveryProbe probe)
    {
        Host = host;
        Principal = principal;
        Logs = logs;
        Probe = probe;
    }

    public IHost Host { get; }
    public IServiceProvider Services => Host.Services;
    public string Principal { get; }
    public CapturingLoggerProvider Logs { get; }
    public DeliveryProbe Probe { get; }
    public TestAmbientTenant CurrentTenant => (TestAmbientTenant)Services.GetRequiredService<ICurrentTenant>();

    /// <summary>Everything logged at Warning or above while the host started.</summary>
    public IReadOnlyList<CapturedLog> StartupWarnings { get; private set; } = [];

    public static Task<WolverineTestHost> StartAsync(
        string connectionString,
        IReadOnlyDictionary<string, string?> settings,
        string principal,
        TenantWrightMessageCatalog catalog) =>
        StartAsync(connectionString, settings, principal, (services, configuration) =>
        {
            // The composition AddTenantWrightMessagingHost performs, over a test catalog instead of the reviewed topology.
            services.AddTenantWrightMessagingCore(configuration, catalog);
            new WolverineMessagingProvider().Register(services, configuration, catalog);
            services.AddTenantWrightMessagingPersistence<WolverineTestDbContext>(configuration);
        });

    /// <summary>Composes the real topology for <paramref name="principal"/> through the provider-selecting entry point.</summary>
    public static Task<WolverineTestHost> StartWithRealTopologyAsync(
        string connectionString,
        IReadOnlyDictionary<string, string?> settings,
        string principal) =>
        StartAsync(connectionString, settings, principal, (services, configuration) =>
            services.AddTenantWrightMessagingHost<WolverineTestDbContext>(configuration, principal));

    private static async Task<WolverineTestHost> StartAsync(
        string connectionString,
        IReadOnlyDictionary<string, string?> settings,
        string principal,
        Action<IServiceCollection, IConfiguration> composeMessaging)
    {
        var logs = new CapturingLoggerProvider();
        var probe = new DeliveryProbe();
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
            EnvironmentName = Environments.Development,
            ApplicationName = principal
        });
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Logging.AddProvider(logs);
        builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions { ValidateScopes = true }));

        var services = builder.Services;
        services.AddHealthChecks();

        // Unit of work and the shared DbContext dependencies, as Shared.Uow and Shared.EntityFrameworkCore provide them.
        services.Configure<UnitOfWorkDefaultOptions>(_ => { });
        services.Configure<SharedDbContextOptions>(_ => { });
        services.AddSingleton<IAmbientUnitOfWork, AmbientUnitOfWork>();
        services.AddSingleton<IUnitOfWorkManager, UnitOfWorkManager>();
        services.AddTransient<IUnitOfWork, UnitOfWork>();
        services.AddSingleton<IConnectionStringResolver>(new FixedConnectionStringResolver(connectionString));
        services.AddSingleton<ICancellationTokenProvider>(NoCancellationTokenProvider.Instance);
        services.AddSingleton<ICurrentTenant, TestAmbientTenant>();
        services.AddSingleton<IDataFilter, EnabledDataFilter>();
        services.AddSingleton<IAuditPropertySetter, NoAuditPropertySetter>();
        services.AddSingleton<IAuditingManager, NoAuditingManager>();
        services.AddSingleton<IEfCoreDbContextTypeProvider, IdentityDbContextTypeProvider>();
        services.AddDbContext<WolverineTestDbContext>(
            options => options.UseNpgsql(connectionString),
            ServiceLifetime.Transient,
            ServiceLifetime.Singleton);
        services.AddTransient(typeof(IDbContextProvider<>), typeof(UnitOfWorkDbContextProvider<>));

        services.AddSingleton(probe);
        services.AddScoped<ITenantWrightMessageHandler<NotificationSendRequestedTenantEto>, TenantCommandHandler>();
        services.AddScoped<ITenantWrightMessageHandler<PermissionGrantsChangedTenantEto>, GrantsChangedHandler>();

        composeMessaging(services, builder.Configuration);

        var host = builder.Build();
        var testHost = new WolverineTestHost(host, principal, logs, probe);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await host.StartAsync(timeout.Token);
        testHost.StartupWarnings = logs.AtOrAbove(LogLevel.Warning);
        return testHost;
    }

    public async Task<HealthReport> ReadinessAsync() =>
        await Services.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(registration => registration.Tags.Contains("ready"));

    /// <summary>Runs <paramref name="work"/> in a new transactional unit of work under <paramref name="tenantId"/>.</summary>
    public async Task InUnitOfWorkAsync(Func<IServiceProvider, Task> work, Guid? tenantId)
    {
        using var tenant = CurrentTenant.Change(tenantId);
        await using var scope = Services.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
        using var unitOfWork = manager.Begin(new UnitOfWorkOptions { IsTransactional = true }, requiresNew: true);
        await work(scope.ServiceProvider);
        await unitOfWork.CompleteAsync();
    }

    /// <summary>Stamps <paramref name="message"/> exactly as the outbox sender would, under <paramref name="tenantId"/>.</summary>
    public TenantWrightOutboundMessage Stamp(object message, TenantWrightMessageRouteKind kind, Guid? tenantId)
    {
        using var tenant = CurrentTenant.Change(tenantId);
        using var scope = Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<TenantWrightOutboundMessageStamper>().Stamp(message, kind);
    }

    public WolverineTestDbContext ReadContext() => Services.GetRequiredService<WolverineTestDbContext>();

    public async Task<MessagingOutboxMessage?> OutboxRowAsync(Func<MessagingOutboxMessage, bool> match)
    {
        await using var dbContext = ReadContext();
        return (await dbContext.OutboxMessages.AsNoTracking().ToListAsync()).SingleOrDefault(match);
    }

    public async Task<IReadOnlyList<MessagingInboxRecord>> InboxAsync(Guid messageId)
    {
        await using var dbContext = ReadContext();
        return await dbContext.InboxRecords.AsNoTracking().Where(record => record.MessageId == messageId).ToListAsync();
    }

    public async Task<int> EffectCountAsync(string key)
    {
        await using var dbContext = ReadContext();
        return await dbContext.ConsumerEffects.AsNoTracking().CountAsync(effect => effect.Key == key);
    }

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await Host.StopAsync(timeout.Token);
        }
        finally
        {
            Host.Dispose();
        }
    }

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
