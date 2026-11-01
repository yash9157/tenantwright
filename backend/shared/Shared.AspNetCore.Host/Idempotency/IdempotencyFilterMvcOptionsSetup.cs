using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Shared.AspNetCore.Host.Idempotency;

internal sealed class IdempotencyFilterMvcOptionsSetup : IConfigureOptions<MvcOptions>
{
    public void Configure(MvcOptions options)
    {
        options.Filters.AddService<IdempotencyActionFilter>(order: int.MinValue + 200);
    }
}
