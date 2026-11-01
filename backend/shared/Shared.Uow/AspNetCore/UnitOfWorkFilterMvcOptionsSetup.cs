using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Shared.Uow.AspNetCore;

internal sealed class UnitOfWorkFilterMvcOptionsSetup : IConfigureOptions<MvcOptions>
{
    private const int FilterOrder = int.MinValue + 100;

    public void Configure(MvcOptions options)
    {
        options.Filters.AddService<UnitOfWorkActionFilter>(FilterOrder);
        options.Filters.AddService<UnitOfWorkPageFilter>(FilterOrder);
    }
}
