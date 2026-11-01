using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Shared.AuditLogs.AspNetCore;

internal sealed class SharedAuditingFilterMvcOptionsSetup : IConfigureOptions<MvcOptions>
{
    public void Configure(MvcOptions options)
    {
        options.Filters.AddService<SharedAuditActionFilter>();
        options.Filters.AddService<SharedAuditPageFilter>();
    }
}
