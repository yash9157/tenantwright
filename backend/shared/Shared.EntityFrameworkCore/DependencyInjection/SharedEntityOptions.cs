using System;
using System.Collections.Generic;
using System.Linq;

using Shared.Domain.Entities;

namespace Shared.EntityFrameworkCore.DependencyInjection;

public class SharedEntityOptions<TEntity>
    where TEntity : IEntity
{
    public Func<IQueryable<TEntity>, IQueryable<TEntity>>? DefaultWithDetailsFunc { get; set; }
}

public class SharedEntityOptions
{
    private readonly IDictionary<Type, object> _options;

    public SharedEntityOptions()
    {
        _options = new Dictionary<Type, object>();
    }

    public SharedEntityOptions<TEntity>? GetOrNull<TEntity>()
        where TEntity : IEntity
    {
        return _options.GetOrDefault(typeof(TEntity)) as SharedEntityOptions<TEntity>;
    }

    public void Entity<TEntity>(Action<SharedEntityOptions<TEntity>> optionsAction)
        where TEntity : IEntity
    {
        // Check.NotNull(optionsAction, nameof(optionsAction));

        optionsAction(
            (_options.GetOrAdd(
                typeof(TEntity),
                () => new SharedEntityOptions<TEntity>()
            ) as SharedEntityOptions<TEntity>)!
        );
    }
}
