using AuditLogs.Domain.Auditing;
using AuditLogs.Domain.Shared.Auditing;
using AuditLogs.EntityFrameworkCore.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace AuditLogs.Application.Auditing;

internal static class AuditLogQueryBuilder
{
    public static IQueryable<AuditLog> Build(
        AuditLogsDbContextApp dbContext,
        AuditLogFilter filter,
        bool includeDetails = false)
    {
        return dbContext.Set<AuditLog>()
            .AsNoTracking()
            .IncludeDetails(includeDetails)
            .ApplyFilter(filter);
    }
}
