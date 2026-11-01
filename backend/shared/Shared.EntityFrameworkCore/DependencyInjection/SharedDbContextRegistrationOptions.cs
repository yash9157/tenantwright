using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Shared.Modularity.DependencyInjection;
using Shared.Domain.Entities;

namespace Shared.EntityFrameworkCore.DependencyInjection;

public class SharedDbContextRegistrationOptions : SharedCommonDbContextRegistrationOptions, ISharedDbContextRegistrationOptionsBuilder
{
    public Dictionary<Type, object> SharedEntityOptions { get; }

    public SharedDbContextRegistrationOptions(Type originalDbContextType, IServiceCollection services)
        : base(originalDbContextType, services)
    {
        SharedEntityOptions = new Dictionary<Type, object>();
    }

    public void Entity<TEntity>(Action<SharedEntityOptions<TEntity>> optionsAction) where TEntity : IEntity
    {
        Services.Configure<SharedEntityOptions>(options =>
        {
            options.Entity(optionsAction);
        });
    }
}
