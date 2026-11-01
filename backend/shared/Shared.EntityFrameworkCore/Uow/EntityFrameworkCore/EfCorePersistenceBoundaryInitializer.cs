using Microsoft.EntityFrameworkCore;
using Shared.Threading;
using Shared.Uow.Abstractions;

namespace Shared.EntityFrameworkCore.Uow.EntityFrameworkCore;

public sealed class EfCorePersistenceBoundaryInitializer<TDbContext>(
    TDbContext dbContext,
    IUnitOfWorkManager unitOfWorkManager,
    ICancellationTokenProvider cancellationTokenProvider) : IPersistenceBoundaryInitializer
    where TDbContext : class, IEfCoreDbContext
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var unitOfWork = unitOfWorkManager.Current
            ?? throw new InvalidOperationException("A persistence boundary requires an ambient unit of work.");
        var databaseApiKey = EfCorePersistenceBoundaryKey.For(typeof(TDbContext));
        if (unitOfWork.FindDatabaseApi(databaseApiKey) is not null)
        {
            return;
        }

        if (dbContext is ISharedEfCoreDbContext sharedDbContext)
        {
            sharedDbContext.Initialize(new EfCoreDbContextInitializationContext(unitOfWork));
        }

        unitOfWork.AddDatabaseApi(databaseApiKey, new EfCoreDatabaseApi(dbContext));
        if (unitOfWork.Options.IsTransactional && dbContext is DbContext efDbContext)
        {
            var transaction = await efDbContext.Database.BeginTransactionAsync(cancellationToken);
            unitOfWork.AddTransactionApi(
                $"EntityFrameworkCore_Direct_{typeof(TDbContext).FullName}",
                new EfCoreTransactionApi(transaction, dbContext, cancellationTokenProvider));
        }
    }
}

internal static class EfCorePersistenceBoundaryKey
{
    public static string For(Type dbContextType)
    {
        ArgumentNullException.ThrowIfNull(dbContextType);
        return $"Direct_{dbContextType.FullName}";
    }
}
