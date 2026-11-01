using System;
using Microsoft.Extensions.Options;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;

namespace Shared.EntityFrameworkCore;

public class EfCoreDbContextTypeProvider : IEfCoreDbContextTypeProvider, ITransientDependency
{
    private readonly SharedDbContextOptions _options;
    private readonly ICurrentTenant _currentTenant;

    public EfCoreDbContextTypeProvider(IOptions<SharedDbContextOptions> options, ICurrentTenant currentTenant)
    {
        _currentTenant = currentTenant;
        _options = options.Value;
    }

    public virtual Type GetDbContextType(Type dbContextType)
    {
        return _options.GetReplacedTypeOrSelf(dbContextType, _currentTenant.GetMultiTenancySide());
    }
}
