using AuditLogs.EntityFrameworkCore.EntityFrameworkCore;
using Castle.DynamicProxy;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shared.AuditLogs.Auditing;
using Shared.Data.ConnectionStrings;
using Shared.Data.Filtering;
using Shared.Domain.Repositories;
using Shared.EntityFrameworkCore;
using Shared.EntityFrameworkCore.Repositories;
using Shared.EntityFrameworkCore.Uow.EntityFrameworkCore;
using Shared.MultiTenancy;
using Shared.Threading;
using Shared.Uow;
using Shared.Uow.Abstractions;
using Shared.Uow.Context;
using Shared.Uow.Implementations;
using Shared.Uow.Options;

namespace AuditLogs.Tests.TestDoubles;

internal sealed class AuditLogsUnitOfWorkHarness : IAsyncDisposable
{
    private readonly SqliteConnection _connection;

    public ServiceProvider Services { get; }
    public IUnitOfWorkExecutor Executor { get; }
    public IUnitOfWorkManager UnitOfWorkManager { get; }
    public IDbContextProvider<AuditLogsDbContextApp> DbContextProvider { get; }

    public AuditLogsUnitOfWorkHarness(bool dataFiltersEnabled = false)
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var dbContextOptions = new DbContextOptionsBuilder<AuditLogsDbContextApp>()
            .UseSqlite(_connection)
            .Options;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.Configure<UnitOfWorkDefaultOptions>(_ => { });
        services.Configure<SharedDbContextOptions>(_ => { });
        services.AddSingleton<IAmbientUnitOfWork, AmbientUnitOfWork>();
        services.AddSingleton<IUnitOfWorkManager, UnitOfWorkManager>();
        services.AddTransient<IUnitOfWork, UnitOfWork>();
        services.AddSingleton<IConnectionStringResolver>(new StaticConnectionStringResolver("auditlogs-sqlite"));
        services.AddSingleton<ICancellationTokenProvider, TestCancellationTokenProvider>();
        services.AddSingleton<ICurrentTenant>(new TestCurrentTenant(null));
        services.AddSingleton<IDataFilter>(new ConfigurableDataFilter(dataFiltersEnabled));
        services.AddSingleton<IAuditPropertySetter>(new NoopAuditPropertySetter());
        services.AddSingleton<IAuditingManager>(new NoopAuditingManager());
        services.AddSingleton<IEfCoreDbContextTypeProvider, StaticDbContextTypeProvider>();
        services.AddSingleton<IUnitOfWorkSuppressionContext, UnitOfWorkSuppressionContext>();
        services.AddTransient(sp => new AuditLogsDbContextApp(dbContextOptions) { ServiceProvider = sp });
        services.AddTransient<IDbContextProvider<AuditLogsDbContextApp>, UnitOfWorkDbContextProvider<AuditLogsDbContextApp>>();

        Services = services.BuildServiceProvider(validateScopes: true);
        UnitOfWorkManager = Services.GetRequiredService<IUnitOfWorkManager>();
        DbContextProvider = Services.GetRequiredService<IDbContextProvider<AuditLogsDbContextApp>>();

        var interceptor = new UnitOfWorkInterceptor(
            UnitOfWorkManager,
            Services.GetRequiredService<IUnitOfWorkSuppressionContext>(),
            Options.Create(new UnitOfWorkDefaultOptions()));
        Executor = new ProxyGenerator().CreateInterfaceProxyWithTarget<IUnitOfWorkExecutor>(
            new UnitOfWorkExecutor(),
            interceptor);

        using var dbContext = CreateDbContext();
        dbContext.Database.EnsureCreated();
    }

    public AuditLogsDbContextApp CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AuditLogsDbContextApp>()
            .UseSqlite(_connection)
            .Options;
        var dbContext = new AuditLogsDbContextApp(options)
        {
            ServiceProvider = Services
        };
        dbContext.Initialize(new EfCoreDbContextInitializationContext(new NoopUnitOfWork(new UnitOfWorkOptions())));
        return dbContext;
    }

    public IRepository<TEntity, long> CreateRepository<TEntity>()
        where TEntity : class, Shared.Domain.Entities.IEntity<long>
    {
        return new EfCoreRepository<AuditLogsDbContextApp, TEntity, long>(DbContextProvider, Services);
    }

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private sealed class StaticConnectionStringResolver : IConnectionStringResolver
    {
        private readonly string _connectionString;

        public StaticConnectionStringResolver(string connectionString)
        {
            _connectionString = connectionString;
        }

        public Task<string> ResolveAsync(string? connectionStringName = null)
            => Task.FromResult(_connectionString);
    }

    private sealed class StaticDbContextTypeProvider : IEfCoreDbContextTypeProvider
    {
        public Type GetDbContextType(Type dbContextType) => dbContextType;
    }

    private sealed class TestCancellationTokenProvider : ICancellationTokenProvider
    {
        public CancellationToken Token => CancellationToken.None;
    }
}
