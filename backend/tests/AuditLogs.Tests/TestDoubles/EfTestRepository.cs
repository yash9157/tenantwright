using Microsoft.EntityFrameworkCore;
using Shared.Domain.Entities;
using Shared.Domain.Repositories;

namespace AuditLogs.Tests.TestDoubles;

/// <summary>
/// Minimal <see cref="IRepository{TEntity, TKey}"/> backed directly by a real EF Core DbContext (SQLite
/// in tests), so tests can construct concrete application services without a full DI container.
/// </summary>
internal sealed class EfTestRepository<TEntity, TKey> : IRepository<TEntity, TKey>
    where TEntity : class, IEntity<TKey>
{
    private readonly DbContext _dbContext;

    public EfTestRepository(DbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public string? EntityName { get; set; }
    public string ProviderName => "EfCoreSqlite";

    public Task<IQueryable<TEntity>> WithDetailsAsync()
        => Task.FromResult(_dbContext.Set<TEntity>().AsQueryable());

    public Task<IQueryable<TEntity>> WithDetailsAsync(params System.Linq.Expressions.Expression<Func<TEntity, object>>[] propertySelectors)
        => Task.FromResult(_dbContext.Set<TEntity>().AsQueryable());

    public Task<IQueryable<TEntity>> GetQueryableAsync()
        => Task.FromResult(_dbContext.Set<TEntity>().AsQueryable());

    public Task<TEntity?> FindAsync(TKey id, bool includeDetails = true, CancellationToken cancellationToken = default)
        => _dbContext.Set<TEntity>().FindAsync([id], cancellationToken).AsTask();

    public async Task<TEntity> GetAsync(TKey id, bool includeDetails = true, CancellationToken cancellationToken = default)
    {
        var entity = await FindAsync(id, includeDetails, cancellationToken);
        return entity ?? throw new InvalidOperationException("Entity not found.");
    }

    public Task<TEntity?> FindAsync(
        System.Linq.Expressions.Expression<Func<TEntity, bool>> predicate,
        bool includeDetails = true,
        CancellationToken cancellationToken = default)
        => _dbContext.Set<TEntity>().FirstOrDefaultAsync(predicate, cancellationToken);

    public async Task<TEntity> GetAsync(
        System.Linq.Expressions.Expression<Func<TEntity, bool>> predicate,
        bool includeDetails = true,
        CancellationToken cancellationToken = default)
    {
        var entity = await FindAsync(predicate, includeDetails, cancellationToken);
        return entity ?? throw new InvalidOperationException("Entity not found.");
    }

    public async Task DeleteAsync(
        System.Linq.Expressions.Expression<Func<TEntity, bool>> predicate,
        bool autoSave = false,
        CancellationToken cancellationToken = default)
    {
        var entities = await _dbContext.Set<TEntity>().Where(predicate).ToListAsync(cancellationToken);
        _dbContext.Set<TEntity>().RemoveRange(entities);
        if (autoSave)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<TEntity> InsertAsync(TEntity entity, bool autoSave = false, CancellationToken cancellationToken = default)
    {
        await _dbContext.Set<TEntity>().AddAsync(entity, cancellationToken);
        if (autoSave)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        return entity;
    }

    public async Task InsertManyAsync(IEnumerable<TEntity> entities, bool autoSave = false, CancellationToken cancellationToken = default)
    {
        await _dbContext.Set<TEntity>().AddRangeAsync(entities, cancellationToken);
        if (autoSave)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    public Task<TEntity> UpdateAsync(TEntity entity, bool autoSave = false, CancellationToken cancellationToken = default)
    {
        _dbContext.Set<TEntity>().Update(entity);
        return autoSave ? SaveAndReturnAsync(entity, cancellationToken) : Task.FromResult(entity);
    }

    public Task UpdateManyAsync(IEnumerable<TEntity> entities, bool autoSave = false, CancellationToken cancellationToken = default)
    {
        _dbContext.Set<TEntity>().UpdateRange(entities);
        return autoSave ? _dbContext.SaveChangesAsync(cancellationToken) : Task.CompletedTask;
    }

    public Task DeleteAsync(TEntity entity, bool autoSave = false, CancellationToken cancellationToken = default)
    {
        _dbContext.Set<TEntity>().Remove(entity);
        return autoSave ? _dbContext.SaveChangesAsync(cancellationToken) : Task.CompletedTask;
    }

    public Task DeleteManyAsync(IEnumerable<TEntity> entities, bool autoSave = false, CancellationToken cancellationToken = default)
    {
        _dbContext.Set<TEntity>().RemoveRange(entities);
        return autoSave ? _dbContext.SaveChangesAsync(cancellationToken) : Task.CompletedTask;
    }

    public Task DeleteAsync(TKey id, bool autoSave = false, CancellationToken cancellationToken = default)
        => DeleteAsync(x => EqualityComparer<TKey>.Default.Equals(x.Id, id), autoSave, cancellationToken);

    public Task DeleteManyAsync(IEnumerable<TKey> ids, bool autoSave = false, CancellationToken cancellationToken = default)
    {
        var set = ids.ToHashSet();
        return DeleteAsync(x => set.Contains(x.Id), autoSave, cancellationToken);
    }

    public Task DeleteDirectAsync(
        System.Linq.Expressions.Expression<Func<TEntity, bool>> predicate,
        CancellationToken cancellationToken = default)
        => _dbContext.Set<TEntity>().Where(predicate).ExecuteDeleteAsync(cancellationToken);

    public Task<List<TEntity>> GetListAsync(bool includeDetails = false, CancellationToken cancellationToken = default)
        => _dbContext.Set<TEntity>().ToListAsync(cancellationToken);

    public Task<List<TEntity>> GetListAsync(
        System.Linq.Expressions.Expression<Func<TEntity, bool>> predicate,
        bool includeDetails = false,
        CancellationToken cancellationToken = default)
        => _dbContext.Set<TEntity>().Where(predicate).ToListAsync(cancellationToken);

    public Task<long> GetCountAsync(CancellationToken cancellationToken = default)
        => _dbContext.Set<TEntity>().LongCountAsync(cancellationToken);

    public Task<List<TEntity>> GetPagedListAsync(
        int skipCount,
        int maxResultCount,
        string sorting,
        bool includeDetails = false,
        CancellationToken cancellationToken = default)
        => _dbContext.Set<TEntity>().Skip(skipCount).Take(maxResultCount).ToListAsync(cancellationToken);

    private async Task<TEntity> SaveAndReturnAsync(TEntity entity, CancellationToken cancellationToken)
    {
        await _dbContext.SaveChangesAsync(cancellationToken);
        return entity;
    }
}
