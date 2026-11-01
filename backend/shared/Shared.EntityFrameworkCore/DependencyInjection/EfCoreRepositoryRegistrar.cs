using System;
using System.Collections.Generic;
using Shared.Domain.Repositories;
using Shared.EntityFrameworkCore.Repositories;
using Shared.Modularity.DependencyInjection;

namespace Shared.EntityFrameworkCore.DependencyInjection;

public class EfCoreRepositoryRegistrar : RepositoryRegistrarBase<SharedDbContextRegistrationOptions>
{
    public EfCoreRepositoryRegistrar(SharedDbContextRegistrationOptions options)
        : base(options)
    {

    }

    protected override IEnumerable<Type> GetEntityTypes(Type dbContextType)
    {
        return DbContextHelper.GetEntityTypes(dbContextType);
    }

    protected override Type GetRepositoryType(Type dbContextType, Type entityType)
    {
        return typeof(EfCoreRepository<,>).MakeGenericType(dbContextType, entityType);
    }

    protected override Type GetRepositoryType(Type dbContextType, Type entityType, Type primaryKeyType)
    {
        return typeof(EfCoreRepository<,,>).MakeGenericType(dbContextType, entityType, primaryKeyType);
    }
}
