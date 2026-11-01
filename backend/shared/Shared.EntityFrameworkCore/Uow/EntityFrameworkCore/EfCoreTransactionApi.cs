using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Shared.Uow.Abstractions;
using Shared.Threading;
using Shared.Modularity.Extensions;

namespace Shared.EntityFrameworkCore.Uow.EntityFrameworkCore;

public class EfCoreTransactionApi : ITransactionApi, ISupportsRollback
{
    public IDbContextTransaction DbContextTransaction { get; }
    public IEfCoreDbContext StarterDbContext { get; }
    public List<IEfCoreDbContext> AttendedDbContexts { get; }

    protected ICancellationTokenProvider CancellationTokenProvider { get; }

    public EfCoreTransactionApi(
        IDbContextTransaction dbContextTransaction,
        IEfCoreDbContext starterDbContext,
        ICancellationTokenProvider cancellationTokenProvider)
    {
        DbContextTransaction = dbContextTransaction;
        StarterDbContext = starterDbContext;
        CancellationTokenProvider = cancellationTokenProvider;
        AttendedDbContexts = new List<IEfCoreDbContext>();
    }

    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        foreach (var dbContext in AttendedDbContexts)
        {
            var db = dbContext as DbContext;
            if (db!.Database.IsRelational() &&
                db.Database.GetDbConnection() == DbContextTransaction.GetDbTransaction().Connection)
            {
                continue; //Relational databases use the shared transaction if they are using the same connection
            }

            await db.Database.CommitTransactionAsync(CancellationTokenProvider.FallbackToProvider(cancellationToken));
        }

        await DbContextTransaction.CommitAsync(CancellationTokenProvider.FallbackToProvider(cancellationToken));
    }

    public void Dispose()
    {
        DbContextTransaction.Dispose();
        GC.SuppressFinalize(this);
    }

    public async Task RollbackAsync(CancellationToken cancellationToken)
    {
        foreach (var dbContext in AttendedDbContexts)
        {
            var db = dbContext as DbContext;
            if (db!.Database.IsRelational() &&
                db.Database.GetDbConnection() == DbContextTransaction.GetDbTransaction().Connection)
            {
                continue; //Relational databases use the shared transaction if they are using the same connection
            }

            await db.Database.RollbackTransactionAsync(CancellationTokenProvider.FallbackToProvider(cancellationToken));
        }

        await DbContextTransaction.RollbackAsync(CancellationTokenProvider.FallbackToProvider(cancellationToken));
    }
}
