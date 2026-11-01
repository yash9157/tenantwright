using Shared.Data.Filtering;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;

namespace Shared.EntityFrameworkCore.GlobalFilters;

public interface ISharedEfCoreDbFunctionContext
{
    IServiceProvider ServiceProvider { get; set; }

    ICurrentTenant CurrentTenant { get; }

    IDataFilter DataFilter { get; }

    string GetCompiledQueryCacheKey();
}
