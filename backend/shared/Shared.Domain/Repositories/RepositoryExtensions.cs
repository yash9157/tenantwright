using Shared.Data.Filtering;
using Shared.Domain.Entities;
using Shared.Modularity;
using Shared.Uow.Abstractions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Shared.Domain.Repositories;

public static class RepositoryExtensions
{
    public static async Task EnsureCollectionLoadedAsync<TEntity, TProperty>(
        this IBasicRepository<TEntity> repository,
        TEntity entity,
        Expression<Func<TEntity, IEnumerable<TProperty>>> propertyExpression,
        CancellationToken cancellationToken = default)
        where TEntity : class, IEntity
        where TProperty : class
    {
        if (repository is ISupportsExplicitLoading<TEntity> explicitLoadingRepository)
        {
            await explicitLoadingRepository.EnsureCollectionLoadedAsync(entity, propertyExpression, cancellationToken);
        }
    }

    public static async Task EnsurePropertyLoadedAsync<TEntity, TKey, TProperty>(
        this IBasicRepository<TEntity, TKey> repository,
        TEntity entity,
        Expression<Func<TEntity, TProperty?>> propertyExpression,
        CancellationToken cancellationToken = default)
        where TEntity : class, IEntity<TKey>
        where TProperty : class
    {
        if (repository is ISupportsExplicitLoading<TEntity> explicitLoadingRepository)
        {
            await explicitLoadingRepository.EnsurePropertyLoadedAsync(entity, propertyExpression, cancellationToken);
        }
    }

    public static async Task EnsureExistsAsync<TEntity, TKey>(
        this IRepository<TEntity, TKey> repository,
        TKey id,
        CancellationToken cancellationToken = default)
        where TEntity : class, IEntity<TKey>
    {
        if (await repository.FindAsync(id, includeDetails: false, cancellationToken) == null)
        {
            throw new EntityNotFoundException<TEntity>(id);
        }
    }

    public static async Task EnsureExistsAsync<TEntity>(
        this IRepository<TEntity> repository,
        Expression<Func<TEntity, bool>> expression,
        CancellationToken cancellationToken = default)
        where TEntity : class, IEntity
    {
        if (await repository.FindAsync(expression, includeDetails: false, cancellationToken) == null)
        {
            throw new EntityNotFoundException<TEntity>();
        }
    }

    public static async Task HardDeleteAsync<TEntity>(
        this IRepository<TEntity> repository,
        Expression<Func<TEntity, bool>> predicate,
        bool autoSave = false,
        CancellationToken cancellationToken = default)
        where TEntity : class, IEntity, ISoftDelete
    {
        var uowManager = repository.GetUnitOfWorkManager();

        if (uowManager.Current == null)
        {
            using var uow = uowManager.Begin();
            await HardDeleteWithUnitOfWorkAsync(repository, predicate, autoSave, uowManager.Current!, cancellationToken);
            await uow.CompleteAsync(cancellationToken);
            return;
        }

        await HardDeleteWithUnitOfWorkAsync(repository, predicate, autoSave, uowManager.Current, cancellationToken);
    }

    public static async Task HardDeleteAsync<TEntity>(
        this IBasicRepository<TEntity> repository,
        IEnumerable<TEntity> entities,
        bool autoSave = false,
        CancellationToken cancellationToken = default)
        where TEntity : class, IEntity, ISoftDelete
    {
        var uowManager = repository.GetUnitOfWorkManager();

        if (uowManager.Current == null)
        {
            using var uow = uowManager.Begin();
            await HardDeleteWithUnitOfWorkAsync(repository, entities, autoSave, uowManager.Current!, cancellationToken);
            await uow.CompleteAsync(cancellationToken);
            return;
        }

        await HardDeleteWithUnitOfWorkAsync(repository, entities, autoSave, uowManager.Current, cancellationToken);
    }

    public static async Task HardDeleteAsync<TEntity>(
        this IBasicRepository<TEntity> repository,
        TEntity entity,
        bool autoSave = false,
        CancellationToken cancellationToken = default)
        where TEntity : class, IEntity, ISoftDelete
    {
        var uowManager = repository.GetUnitOfWorkManager();

        if (uowManager.Current == null)
        {
            using var uow = uowManager.Begin();
            await HardDeleteWithUnitOfWorkAsync(repository, entity, autoSave, uowManager.Current!, cancellationToken);
            await uow.CompleteAsync(cancellationToken);
            return;
        }

        await HardDeleteWithUnitOfWorkAsync(repository, entity, autoSave, uowManager.Current, cancellationToken);
    }

    public static TRepository SetEntityName<TRepository>(this TRepository repository, string name)
        where TRepository : class, IRepository
    {
        repository.EntityName = name;
        return repository;
    }

    private static IUnitOfWorkManager GetUnitOfWorkManager<TEntity>(
        this IBasicRepository<TEntity> repository)
        where TEntity : class, IEntity
    {
        if (repository is not IUnitOfWorkManagerAccessor unitOfWorkManagerAccessor)
        {
            throw new InvalidOperationException(
                $"Repository '{repository.GetType().AssemblyQualifiedName}' must implement {typeof(IUnitOfWorkManagerAccessor).AssemblyQualifiedName}.");
        }

        if (unitOfWorkManagerAccessor.UnitOfWorkManager == null)
        {
            throw new InvalidOperationException($"{nameof(unitOfWorkManagerAccessor.UnitOfWorkManager)} is null.");
        }

        return unitOfWorkManagerAccessor.UnitOfWorkManager;
    }

    private static async Task HardDeleteWithUnitOfWorkAsync<TEntity>(
        IRepository<TEntity> repository,
        Expression<Func<TEntity, bool>> predicate,
        bool autoSave,
        IUnitOfWork currentUow,
        CancellationToken cancellationToken)
        where TEntity : class, IEntity, ISoftDelete
    {
        var dataFilter = currentUow.ServiceProvider.GetService(typeof(IDataFilter<ISoftDelete>)) as IDataFilter<ISoftDelete>;
        if (dataFilter != null)
        {
            using (dataFilter.Disable())
            {
                var entities = await repository.GetListAsync(predicate, cancellationToken: cancellationToken);
                await HardDeleteWithUnitOfWorkAsync(repository, entities, autoSave, currentUow, cancellationToken);
                return;
            }
        }

        var fallbackEntities = await repository.GetListAsync(predicate, cancellationToken: cancellationToken);
        await HardDeleteWithUnitOfWorkAsync(repository, fallbackEntities, autoSave, currentUow, cancellationToken);
    }

    private static async Task HardDeleteWithUnitOfWorkAsync<TEntity>(
        IBasicRepository<TEntity> repository,
        IEnumerable<TEntity> entities,
        bool autoSave,
        IUnitOfWork currentUow,
        CancellationToken cancellationToken)
        where TEntity : class, IEntity, ISoftDelete
    {
        HashSet<IEntity> hardDeleteEntities;
        if (currentUow.Items.TryGetValue(UnitOfWorkItemNames.HardDeletedEntities, out var existingValue) &&
            existingValue is HashSet<IEntity> existingSet)
        {
            hardDeleteEntities = existingSet;
        }
        else
        {
            hardDeleteEntities = new HashSet<IEntity>();
        }

        hardDeleteEntities.UnionWith(entities.Cast<IEntity>());
        currentUow.Items[UnitOfWorkItemNames.HardDeletedEntities] = hardDeleteEntities;

        await repository.DeleteManyAsync(entities, autoSave, cancellationToken);
    }

    private static async Task HardDeleteWithUnitOfWorkAsync<TEntity>(
        IBasicRepository<TEntity> repository,
        TEntity entity,
        bool autoSave,
        IUnitOfWork currentUow,
        CancellationToken cancellationToken)
        where TEntity : class, IEntity, ISoftDelete
    {
        HashSet<IEntity> hardDeleteEntities;
        if (currentUow.Items.TryGetValue(UnitOfWorkItemNames.HardDeletedEntities, out var existingValue) &&
            existingValue is HashSet<IEntity> existingSet)
        {
            hardDeleteEntities = existingSet;
        }
        else
        {
            hardDeleteEntities = new HashSet<IEntity>();
        }

        hardDeleteEntities.Add(entity);
        currentUow.Items[UnitOfWorkItemNames.HardDeletedEntities] = hardDeleteEntities;

        await repository.DeleteAsync(entity, autoSave, cancellationToken);
    }
}
