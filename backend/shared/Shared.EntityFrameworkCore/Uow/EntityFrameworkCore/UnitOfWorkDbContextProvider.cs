using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.Data.ConnectionStrings;
using Shared.EntityFrameworkCore.DependencyInjection;
using Shared.EntityFrameworkCore.TenantDatabases;
using Shared.MultiTenancy;
using Shared.Threading;
using Shared.Uow.Abstractions;

namespace Shared.EntityFrameworkCore.Uow.EntityFrameworkCore;

public class UnitOfWorkDbContextProvider<TDbContext> : IDbContextProvider<TDbContext>
    where TDbContext : class, IEfCoreDbContext
{
    private const string TransactionsNotSupportedWarningMessage = "Current database does not support transactions. Your database may remain in an inconsistent state in an error case.";

    public ILogger<UnitOfWorkDbContextProvider<TDbContext>> Logger { get; set; }

    protected IUnitOfWorkManager UnitOfWorkManager { get; }
    protected IConnectionStringResolver ConnectionStringResolver { get; }
    protected ICancellationTokenProvider CancellationTokenProvider { get; }
    protected IEfCoreDbContextTypeProvider EfCoreDbContextTypeProvider { get; }
    protected ICurrentTenant CurrentTenant { get; }

    public UnitOfWorkDbContextProvider(
        IUnitOfWorkManager unitOfWorkManager,
        IConnectionStringResolver connectionStringResolver,
        ICancellationTokenProvider cancellationTokenProvider,
        IEfCoreDbContextTypeProvider efCoreDbContextTypeProvider,
        ICurrentTenant currentTenant)
    {
        UnitOfWorkManager = unitOfWorkManager;
        ConnectionStringResolver = connectionStringResolver;
        CancellationTokenProvider = cancellationTokenProvider;
        EfCoreDbContextTypeProvider = efCoreDbContextTypeProvider;
        CurrentTenant = currentTenant;

        Logger = NullLogger<UnitOfWorkDbContextProvider<TDbContext>>.Instance;
    }

    public virtual async Task<TDbContext> GetDbContextAsync()
    {
        var targetDbContextType = EfCoreDbContextTypeProvider.GetDbContextType(typeof(TDbContext));
        var unitOfWork = UnitOfWorkManager.Current;
        if (unitOfWork == null)
        {
            throw new InvalidOperationException(
                "A DbContext can only be created inside a TenantWright unit of work.");
        }

        var initializedDatabaseApi = unitOfWork.FindDatabaseApi(EfCorePersistenceBoundaryKey.For(targetDbContextType));
        if (initializedDatabaseApi is EfCoreDatabaseApi initializedEfCoreDatabaseApi)
        {
            return (TDbContext)initializedEfCoreDatabaseApi.DbContext;
        }

        var connection = await TenantDatabaseDbContextActivator.ResolveConnectionAsync(
            targetDbContextType,
            ConnectionStringResolver,
            CurrentTenant);

        var dbContextKey = $"{targetDbContextType.FullName}_{connection.ConnectionString}";

        var databaseApi = unitOfWork.FindDatabaseApi(dbContextKey);

        if (databaseApi == null)
        {
            databaseApi = new EfCoreDatabaseApi(await CreateDbContextAsync(unitOfWork, connection));

            unitOfWork.AddDatabaseApi(dbContextKey, databaseApi);
        }

        return (TDbContext)((EfCoreDatabaseApi)databaseApi).DbContext;
    }

    protected virtual async Task<TDbContext> CreateDbContextAsync(IUnitOfWork unitOfWork, TenantDatabaseConnection connection)
    {
        var creationContext = new DbContextCreationContext(connection.ConnectionStringName, connection.ConnectionString)
        {
            TenantDatabaseConnection = connection
        };
        using (DbContextCreationContext.Use(creationContext))
        {
            var dbContext = await CreateDbContextAsync(unitOfWork);

            if (dbContext is ISharedEfCoreDbContext sharedEfCoreDbContext)
            {
                sharedEfCoreDbContext.Initialize(
                    new EfCoreDbContextInitializationContext(
                        unitOfWork
                    )
                );
            }

            return dbContext;
        }
    }

    protected virtual async Task<TDbContext> CreateDbContextAsync(IUnitOfWork unitOfWork)
    {
        return unitOfWork.Options.IsTransactional
            ? await CreateDbContextWithTransactionAsync(unitOfWork)
            : await ActivateDbContextAsync(unitOfWork);
    }

    /// <summary>
    /// The scoped DI instance for the shared database, or a unit-of-work-owned instance bound to the current
    /// tenant's dedicated database after its schema was verified (see <see cref="TenantDatabaseDbContextActivator"/>
    /// and <see cref="TenantDatabaseMigrationGuard"/>).
    /// </summary>
    protected virtual async Task<TDbContext> ActivateDbContextAsync(IUnitOfWork unitOfWork)
    {
        var connection = DbContextCreationContext.Current.TenantDatabaseConnection
            ?? new TenantDatabaseConnection(
                DbContextCreationContext.Current.ConnectionStringName,
                DbContextCreationContext.Current.ConnectionString,
                IsTenantDedicated: false);
        var dbContext = TenantDatabaseDbContextActivator.Activate<TDbContext>(
            unitOfWork.ServiceProvider,
            connection,
            out var owned);
        if (!owned)
        {
            return dbContext;
        }

        unitOfWork.Disposed += (_, _) => (dbContext as IDisposable)?.Dispose();
        await unitOfWork.ServiceProvider
            .GetRequiredService<TenantDatabaseMigrationGuard>()
            .EnsureMigratedAsync((DbContext)(object)dbContext, connection, CurrentTenant.Id!.Value, GetCancellationToken());
        return dbContext;
    }

    protected virtual async Task<TDbContext> CreateDbContextWithTransactionAsync(IUnitOfWork unitOfWork)
    {
        var transactionApiKey = $"EntityFrameworkCore_{DbContextCreationContext.Current.ConnectionString}";
        var activeTransaction = unitOfWork.FindTransactionApi(transactionApiKey) as EfCoreTransactionApi;

        if (activeTransaction == null)
        {
            var dbContext = await ActivateDbContextAsync(unitOfWork);
            var db = dbContext as DbContext;

            try
            {
                var dbTransaction = unitOfWork.Options.IsolationLevel.HasValue
                    ? await db!.Database.BeginTransactionAsync(unitOfWork.Options.IsolationLevel.Value, GetCancellationToken())
                    : await db!.Database.BeginTransactionAsync(GetCancellationToken());

                unitOfWork.AddTransactionApi(
                    transactionApiKey,
                    new EfCoreTransactionApi(
                        dbTransaction,
                        dbContext,
                        CancellationTokenProvider
                    )
                );
            }
            catch (Exception e) when (e is InvalidOperationException || e is NotSupportedException)
            {
                Logger.LogWarning(TransactionsNotSupportedWarningMessage);

                return dbContext;
            }

            return dbContext;
        }

        DbContextCreationContext.Current.ExistingConnection = activeTransaction.DbContextTransaction.GetDbTransaction().Connection;

        var attendedDbContext = await ActivateDbContextAsync(unitOfWork);
        var attendedDatabase = attendedDbContext as DbContext;

        if (attendedDatabase!.Database.IsRelational())
        {
            if (attendedDatabase.Database.GetDbConnection() == DbContextCreationContext.Current.ExistingConnection)
            {
                await attendedDatabase.Database.UseTransactionAsync(activeTransaction.DbContextTransaction.GetDbTransaction(), GetCancellationToken());
            }
            else
            {
                try
                {
                    if (unitOfWork.Options.IsolationLevel.HasValue)
                    {
                        await attendedDatabase.Database.BeginTransactionAsync(
                            unitOfWork.Options.IsolationLevel.Value,
                            GetCancellationToken()
                        );
                    }
                    else
                    {
                        await attendedDatabase.Database.BeginTransactionAsync(
                            GetCancellationToken()
                        );
                    }
                }
                catch (Exception e) when (e is InvalidOperationException || e is NotSupportedException)
                {
                    Logger.LogWarning(TransactionsNotSupportedWarningMessage);

                    return attendedDbContext;
                }
            }
        }
        else
        {
            try
            {
                await attendedDatabase.Database.BeginTransactionAsync(GetCancellationToken());
            }
            catch (Exception e) when (e is InvalidOperationException || e is NotSupportedException)
            {
                Logger.LogWarning(TransactionsNotSupportedWarningMessage);

                return attendedDbContext;
            }
        }

        activeTransaction.AttendedDbContexts.Add(attendedDbContext);

        return attendedDbContext;
    }

    protected virtual CancellationToken GetCancellationToken(CancellationToken preferredValue = default)
    {
        return CancellationTokenProvider.FallbackToProvider(preferredValue);
    }
}
