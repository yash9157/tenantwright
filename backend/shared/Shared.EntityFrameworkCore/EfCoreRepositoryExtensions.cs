using Microsoft.EntityFrameworkCore;
using Shared.Domain.Entities;
using Shared.Domain.Repositories;
using Shared.EntityFrameworkCore.Repositories;
using System;
using System.Threading.Tasks;

namespace Shared.EntityFrameworkCore;

public static class EfCoreRepositoryExtensions
{
    public static Task<DbContext> GetDbContextAsync<TEntity>(this IReadOnlyBasicRepository<TEntity> repository)
        where TEntity : class, IEntity
    {
        return repository.ToEfCoreRepository().GetDbContextAsync();
    }

    public static Task<DbSet<TEntity>> GetDbSetAsync<TEntity>(this IReadOnlyBasicRepository<TEntity> repository)
        where TEntity : class, IEntity
    {
        return repository.ToEfCoreRepository().GetDbSetAsync();
    }

    public static IEfCoreRepository<TEntity> ToEfCoreRepository<TEntity>(this IReadOnlyBasicRepository<TEntity> repository)
        where TEntity : class, IEntity
    {
        if (repository is IEfCoreRepository<TEntity> efCoreRepository)
        {
            return efCoreRepository;
        }

        throw new ArgumentException(
            $"Given repository does not implement {typeof(IEfCoreRepository<TEntity>).AssemblyQualifiedName}.",
            nameof(repository));
    }
}
