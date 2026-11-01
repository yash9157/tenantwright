using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shared.Domain.Entities;
using Shared.Domain.Repositories;
using Shared.EntityFrameworkCore.DependencyInjection;
using Shared.Guids;
using Shared.Modularity.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using System.Linq.Dynamic.Core;

namespace Shared.EntityFrameworkCore.Repositories;

public class EfCoreRepository<TDbContext, TEntity> : RepositoryBase<TEntity>, IEfCoreRepository<TEntity>
    where TDbContext : class, IEfCoreDbContext
    where TEntity : class, IEntity
{
    protected virtual SharedEntityOptions<TEntity> EntityOptions => _entityOptionsLazy.Value;

    protected virtual IGuidGenerator GuidGenerator => ServiceProvider.GetService<IGuidGenerator>() ?? UuidV7GuidGenerator.Instance;

    private readonly IDbContextProvider<TDbContext> _dbContextProvider;
    private readonly Lazy<SharedEntityOptions<TEntity>> _entityOptionsLazy;

    public EfCoreRepository(
        IDbContextProvider<TDbContext> dbContextProvider,
        IServiceProvider serviceProvider)
        : base(SharedEfCoreConsts.ProviderName)
    {
        _dbContextProvider = dbContextProvider;
        ServiceProvider = serviceProvider;
        _entityOptionsLazy = new Lazy<SharedEntityOptions<TEntity>>(
            () => ServiceProvider
                .GetRequiredService<IOptions<SharedEntityOptions>>()
                .Value
                .GetOrNull<TEntity>() ?? new SharedEntityOptions<TEntity>());
    }

    public virtual async Task<DbContext> GetDbContextAsync()
    {
        return (await GetDbContextInternalAsync() as DbContext)!;
    }

    public virtual async Task<DbSet<TEntity>> GetDbSetAsync()
    {
        return GetDbSetInternal(await GetDbContextInternalAsync());
    }

    protected virtual async Task<TDbContext> GetDbContextInternalAsync()
    {
        if (!EntityHelper.IsMultiTenant<TEntity>())
        {
            using (CurrentTenant.Change(null))
            {
                return await _dbContextProvider.GetDbContextAsync();
            }
        }

        return await _dbContextProvider.GetDbContextAsync();
    }

    protected virtual DbSet<TEntity> GetDbSetInternal(TDbContext dbContext)
    {
        return EntityName != null
            ? (dbContext as DbContext)!.Set<TEntity>(EntityName)
            : (dbContext as DbContext)!.Set<TEntity>();
    }

    public override async Task<TEntity> InsertAsync(TEntity entity, bool autoSave = false, CancellationToken cancellationToken = default)
    {
        CheckAndSetId(entity);

        var dbContext = await GetDbContextInternalAsync();
        var dbSet = GetDbSetInternal(dbContext);
        var savedEntity = (await dbSet.AddAsync(entity, GetCancellationToken(cancellationToken))).Entity;

        if (autoSave)
        {
            await (dbContext as DbContext)!.SaveChangesAsync(GetCancellationToken(cancellationToken));
        }

        return savedEntity;
    }

    public override async Task InsertManyAsync(IEnumerable<TEntity> entities, bool autoSave = false, CancellationToken cancellationToken = default)
    {
        var entityArray = entities.ToArray();
        if (entityArray.Length == 0)
        {
            return;
        }

        cancellationToken = GetCancellationToken(cancellationToken);
        foreach (var entity in entityArray)
        {
            CheckAndSetId(entity);
        }

        var dbContext = await GetDbContextInternalAsync();
        await GetDbSetInternal(dbContext).AddRangeAsync(entityArray, cancellationToken);

        if (autoSave)
        {
            await (dbContext as DbContext)!.SaveChangesAsync(cancellationToken);
        }
    }

    public override async Task<TEntity> UpdateAsync(TEntity entity, bool autoSave = false, CancellationToken cancellationToken = default)
    {
        var dbContext = await GetDbContextInternalAsync();
        var dbSet = GetDbSetInternal(dbContext);
        var entry = (dbContext as DbContext)!.Entry(entity);
        if (entry.State == EntityState.Detached)
        {
            dbSet.Attach(entity);
            entry.State = EntityState.Modified;
        }

        if (autoSave)
        {
            await (dbContext as DbContext)!.SaveChangesAsync(GetCancellationToken(cancellationToken));
        }

        return entity;
    }

    public override async Task UpdateManyAsync(IEnumerable<TEntity> entities, bool autoSave = false, CancellationToken cancellationToken = default)
    {
        var entityArray = entities.ToArray();
        if (entityArray.Length == 0)
        {
            return;
        }

        cancellationToken = GetCancellationToken(cancellationToken);

        var dbContext = await GetDbContextInternalAsync();
        GetDbSetInternal(dbContext).UpdateRange(entityArray);

        if (autoSave)
        {
            await (dbContext as DbContext)!.SaveChangesAsync(cancellationToken);
        }
    }

    public override async Task DeleteAsync(TEntity entity, bool autoSave = false, CancellationToken cancellationToken = default)
    {
        var dbContext = await GetDbContextInternalAsync();
        GetDbSetInternal(dbContext).Remove(entity);

        if (autoSave)
        {
            await (dbContext as DbContext)!.SaveChangesAsync(GetCancellationToken(cancellationToken));
        }
    }

    public override async Task DeleteManyAsync(IEnumerable<TEntity> entities, bool autoSave = false, CancellationToken cancellationToken = default)
    {
        var entityArray = entities.ToArray();
        if (entityArray.Length == 0)
        {
            return;
        }

        cancellationToken = GetCancellationToken(cancellationToken);

        var dbContext = await GetDbContextInternalAsync();
        (dbContext as DbContext)!.RemoveRange(entityArray);

        if (autoSave)
        {
            await (dbContext as DbContext)!.SaveChangesAsync(cancellationToken);
        }
    }

    public override async Task<List<TEntity>> GetListAsync(bool includeDetails = false, CancellationToken cancellationToken = default)
    {
        return includeDetails
            ? await (await WithDetailsAsync()).ToListAsync(GetCancellationToken(cancellationToken))
            : await (await GetQueryableAsync()).ToListAsync(GetCancellationToken(cancellationToken));
    }

    public override async Task<List<TEntity>> GetListAsync(Expression<Func<TEntity, bool>> predicate, bool includeDetails = false, CancellationToken cancellationToken = default)
    {
        return includeDetails
            ? await (await WithDetailsAsync()).Where(predicate).ToListAsync(GetCancellationToken(cancellationToken))
            : await (await GetQueryableAsync()).Where(predicate).ToListAsync(GetCancellationToken(cancellationToken));
    }

    public override async Task<long> GetCountAsync(CancellationToken cancellationToken = default)
    {
        return await (await GetQueryableAsync()).LongCountAsync(GetCancellationToken(cancellationToken));
    }

    public override async Task<List<TEntity>> GetPagedListAsync(
        int skipCount,
        int maxResultCount,
        string sorting,
        bool includeDetails = false,
        CancellationToken cancellationToken = default)
    {
        var queryable = includeDetails ? await WithDetailsAsync() : await GetQueryableAsync();
        var ordering = await GetValidatedOrderingAsync(sorting);
        return await queryable
            .OrderBy(ordering)
            .PageBy(skipCount, maxResultCount)
            .ToListAsync(GetCancellationToken(cancellationToken));
    }

    private async Task<string> GetValidatedOrderingAsync(string sorting)
    {
        if (string.IsNullOrWhiteSpace(sorting))
        {
            throw new ArgumentException("Sorting is required for deterministic paging.", nameof(sorting));
        }

        var dbContext = await GetDbContextAsync();
        var entityType = dbContext.Model.FindEntityType(typeof(TEntity))
            ?? throw new InvalidOperationException($"Entity type '{typeof(TEntity).FullName}' is not part of the EF model.");

        var scalarProperties = entityType.GetProperties()
            .Where(static property => property.PropertyInfo != null)
            .ToDictionary(
                static property => property.Name,
                static property => property.Name,
                StringComparer.OrdinalIgnoreCase);

        var clauses = new List<string>();
        var orderedProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawClause in sorting.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = rawClause.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length is < 1 or > 2 ||
                !scalarProperties.TryGetValue(parts[0], out var propertyName) ||
                (parts.Length == 2 &&
                 !string.Equals(parts[1], "asc", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(parts[1], "ascending", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(parts[1], "desc", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(parts[1], "descending", StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException(
                    $"Invalid sorting clause '{rawClause}'. Only top-level mapped scalar properties with optional asc/desc are allowed.",
                    nameof(sorting));
            }

            var direction = parts.Length == 2 &&
                            (string.Equals(parts[1], "desc", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(parts[1], "descending", StringComparison.OrdinalIgnoreCase))
                ? "desc"
                : "asc";
            clauses.Add($"{propertyName} {direction}");
            orderedProperties.Add(propertyName);
        }

        if (clauses.Count == 0)
        {
            throw new ArgumentException("Sorting must contain at least one property.", nameof(sorting));
        }

        var primaryKey = entityType.FindPrimaryKey()
            ?? throw new InvalidOperationException($"Entity type '{typeof(TEntity).FullName}' does not define a primary key.");
        foreach (var property in primaryKey.Properties)
        {
            if (!orderedProperties.Contains(property.Name))
            {
                clauses.Add($"{property.Name} asc");
            }
        }

        return string.Join(", ", clauses);
    }

    public override async Task<IQueryable<TEntity>> GetQueryableAsync()
    {
        // A repository query can be the start of a read-modify-write flow. Keep entity
        // materialization tracked by default so EF persists only the properties actually
        // changed; read-only callers can opt out explicitly with AsNoTracking().
        return (await GetDbSetAsync()).AsQueryable();
    }

    protected override async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        await (await GetDbContextInternalAsync() as DbContext)!.SaveChangesAsync(cancellationToken);
    }

    public override async Task<TEntity?> FindAsync(
        Expression<Func<TEntity, bool>> predicate,
        bool includeDetails = true,
        CancellationToken cancellationToken = default)
    {
        return includeDetails
            ? await (await WithDetailsAsync()).Where(predicate).SingleOrDefaultAsync(GetCancellationToken(cancellationToken))
            : await (await GetQueryableAsync()).Where(predicate).SingleOrDefaultAsync(GetCancellationToken(cancellationToken));
    }

    public override async Task DeleteAsync(Expression<Func<TEntity, bool>> predicate, bool autoSave = false, CancellationToken cancellationToken = default)
    {
        var dbSet = await GetDbSetAsync();
        var entities = await dbSet
            .Where(predicate)
            .ToListAsync(GetCancellationToken(cancellationToken));

        await DeleteManyAsync(entities, autoSave, cancellationToken);
    }

    public override async Task DeleteDirectAsync(Expression<Func<TEntity, bool>> predicate, CancellationToken cancellationToken = default)
    {
        var dbSet = await GetDbSetAsync();
        await dbSet
            .Where(predicate)
            .ExecuteDeleteAsync(GetCancellationToken(cancellationToken));
    }

    public virtual async Task EnsureCollectionLoadedAsync<TProperty>(
        TEntity entity,
        Expression<Func<TEntity, IEnumerable<TProperty>>> propertyExpression,
        CancellationToken cancellationToken = default)
        where TProperty : class
    {
        await (await GetDbContextAsync())
            .Entry(entity)
            .Collection(propertyExpression)
            .LoadAsync(GetCancellationToken(cancellationToken));
    }

    public virtual async Task EnsurePropertyLoadedAsync<TProperty>(
        TEntity entity,
        Expression<Func<TEntity, TProperty?>> propertyExpression,
        CancellationToken cancellationToken = default)
        where TProperty : class
    {
        await (await GetDbContextAsync())
            .Entry(entity)
            .Reference(propertyExpression)
            .LoadAsync(GetCancellationToken(cancellationToken));
    }

    public override async Task<IQueryable<TEntity>> WithDetailsAsync()
    {
        if (EntityOptions.DefaultWithDetailsFunc == null)
        {
            return await base.WithDetailsAsync();
        }

        return EntityOptions.DefaultWithDetailsFunc(await GetQueryableAsync());
    }

    public override async Task<IQueryable<TEntity>> WithDetailsAsync(params Expression<Func<TEntity, object>>[] propertySelectors)
    {
        return IncludeDetails(await GetQueryableAsync(), propertySelectors);
    }

    private static IQueryable<TEntity> IncludeDetails(IQueryable<TEntity> query, Expression<Func<TEntity, object>>[] propertySelectors)
    {
        if (propertySelectors.Length == 0)
        {
            return query;
        }

        foreach (var propertySelector in propertySelectors)
        {
            query = query.Include(propertySelector);
        }

        return query;
    }

    protected virtual void CheckAndSetId(TEntity entity)
    {
        if (entity is IEntity<Guid> entityWithGuidId)
        {
            EntityHelper.TrySetId(entityWithGuidId, () => GuidGenerator.Create(), checkForDisableIdGenerationAttribute: true);
        }
    }
}

public class EfCoreRepository<TDbContext, TEntity, TKey> :
    EfCoreRepository<TDbContext, TEntity>,
    IEfCoreRepository<TEntity, TKey>,
    ISupportsExplicitLoading<TEntity>
    where TDbContext : class, IEfCoreDbContext
    where TEntity : class, IEntity<TKey>
{
    public EfCoreRepository(
        IDbContextProvider<TDbContext> dbContextProvider,
        IServiceProvider serviceProvider)
        : base(dbContextProvider, serviceProvider)
    {
    }

    public virtual async Task<TEntity> GetAsync(TKey id, bool includeDetails = true, CancellationToken cancellationToken = default)
    {
        var entity = await FindAsync(id, includeDetails, GetCancellationToken(cancellationToken));
        if (entity == null)
        {
            throw new EntityNotFoundException<TEntity>(id);
        }

        return entity;
    }

    public virtual async Task<TEntity?> FindAsync(TKey id, bool includeDetails = true, CancellationToken cancellationToken = default)
    {
        if (includeDetails)
        {
            return await (await WithDetailsAsync()).OrderBy(e => e.Id).FirstOrDefaultAsync(e => e.Id!.Equals(id), GetCancellationToken(cancellationToken));
        }

        return await (await GetDbSetAsync()).FindAsync([id!], GetCancellationToken(cancellationToken));
    }

    public virtual async Task DeleteAsync(TKey id, bool autoSave = false, CancellationToken cancellationToken = default)
    {
        var entity = await FindAsync(id, cancellationToken: cancellationToken);
        if (entity == null)
        {
            return;
        }

        await DeleteAsync(entity, autoSave, cancellationToken);
    }

    public virtual async Task DeleteManyAsync(IEnumerable<TKey> ids, bool autoSave = false, CancellationToken cancellationToken = default)
    {
        cancellationToken = GetCancellationToken(cancellationToken);
        var entities = await (await GetDbSetAsync()).Where(x => ids.Contains(x.Id)).ToListAsync(cancellationToken);
        await DeleteManyAsync(entities, autoSave, cancellationToken);
    }
}
