using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace Shared.EntityFrameworkCore;

public class SharedEntityOptions
{
    public IDictionary<Type, object> Options { get; }

    public SharedEntityOptions()
    {
        Options = new Dictionary<Type, object>();
    }

    public SharedEntityOptions<TEntity> GetOrNull<TEntity>()
        where TEntity : class
    {
        if (Options.TryGetValue(typeof(TEntity), out var options))
        {
            return (SharedEntityOptions<TEntity>)options;
        }

        return null!;
    }
}

public class SharedEntityOptions<TEntity>
    where TEntity : class
{
    public Func<IQueryable<TEntity>, IQueryable<TEntity>>? DefaultWithDetailsFunc { get; set; }
}
