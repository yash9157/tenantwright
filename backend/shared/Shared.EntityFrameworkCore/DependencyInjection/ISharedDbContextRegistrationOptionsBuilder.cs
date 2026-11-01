using System;

using Shared.Modularity.DependencyInjection;
using Shared.Domain.Entities;

namespace Shared.EntityFrameworkCore.DependencyInjection;

public interface ISharedDbContextRegistrationOptionsBuilder : ISharedCommonDbContextRegistrationOptionsBuilder
{
    void Entity<TEntity>(Action<SharedEntityOptions<TEntity>> optionsAction)
        where TEntity : IEntity;
}
