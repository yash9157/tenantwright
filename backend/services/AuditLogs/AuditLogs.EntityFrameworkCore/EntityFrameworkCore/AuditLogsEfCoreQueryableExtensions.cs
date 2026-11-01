using AuditLogs.Domain.Auditing;
using AuditLogs.Domain.Shared.Auditing;
using Microsoft.EntityFrameworkCore;

namespace AuditLogs.EntityFrameworkCore.EntityFrameworkCore;

public static class AuditLogsEfCoreQueryableExtensions
{
    public static IQueryable<AuditLog> IncludeDetails(
        this IQueryable<AuditLog> queryable,
        bool include = true)
    {
        if (!include)
        {
            return queryable;
        }

        return queryable
            .Include(x => x.Actions)
            .Include(x => x.EntityChanges)
            .ThenInclude(change => change.PropertyChanges)
            .AsSplitQuery();
    }

    public static IQueryable<EntityChange> IncludeDetails(
        this IQueryable<EntityChange> queryable,
        bool include = true)
    {
        if (!include)
        {
            return queryable;
        }

        return queryable.Include(x => x.PropertyChanges);
    }

    public static IQueryable<AuditLog> ApplyFilter(
        this IQueryable<AuditLog> queryable,
        AuditLogFilter filter)
    {
        if (filter.TenantId.HasValue)
        {
            queryable = queryable.Where(x => x.TenantId == filter.TenantId.Value);
        }

        if (filter.FromUtc.HasValue)
        {
            queryable = queryable.Where(x => x.ExecutionTime >= filter.FromUtc.Value);
        }

        if (filter.ToUtc.HasValue)
        {
            queryable = queryable.Where(x => x.ExecutionTime <= filter.ToUtc.Value);
        }

        if (!string.IsNullOrWhiteSpace(filter.UserId))
        {
            queryable = queryable.Where(x => x.UserId == filter.UserId);
        }

        if (!string.IsNullOrWhiteSpace(filter.ApplicationName))
        {
            queryable = queryable.Where(x => x.ApplicationName == filter.ApplicationName);
        }

        if (!string.IsNullOrWhiteSpace(filter.Category))
        {
            queryable = queryable.Where(x => x.Category == filter.Category);
        }

        if (!string.IsNullOrWhiteSpace(filter.UrlContains))
        {
            queryable = queryable.Where(x => x.Url != null && x.Url.Contains(filter.UrlContains));
        }

        if (filter.HttpStatusCode.HasValue)
        {
            queryable = queryable.Where(x => x.HttpStatusCode == filter.HttpStatusCode.Value);
        }

        if (!string.IsNullOrWhiteSpace(filter.ClientIpAddress))
        {
            queryable = queryable.Where(x => x.ClientIpAddress == filter.ClientIpAddress);
        }

        if (!string.IsNullOrWhiteSpace(filter.CorrelationId))
        {
            queryable = queryable.Where(x => x.CorrelationId == filter.CorrelationId);
        }

        if (!string.IsNullOrWhiteSpace(filter.TraceId))
        {
            queryable = queryable.Where(x => x.TraceId == filter.TraceId);
        }

        return queryable;
    }

    public static IQueryable<EntityChange> ApplyFilter(
        this IQueryable<EntityChange> queryable,
        EntityChangeFilter filter)
    {
        if (filter.TenantId.HasValue)
        {
            queryable = queryable.Where(x => x.TenantId == filter.TenantId.Value);
        }

        if (filter.AuditLogId.HasValue)
        {
            queryable = queryable.Where(x => x.AuditLogId == filter.AuditLogId.Value);
        }

        if (filter.FromUtc.HasValue)
        {
            queryable = queryable.Where(x => x.ChangeTime >= filter.FromUtc.Value);
        }

        if (filter.ToUtc.HasValue)
        {
            queryable = queryable.Where(x => x.ChangeTime <= filter.ToUtc.Value);
        }

        if (!string.IsNullOrWhiteSpace(filter.EntityTypeFullName))
        {
            queryable = queryable.Where(x => x.EntityTypeFullName == filter.EntityTypeFullName);
        }

        if (!string.IsNullOrWhiteSpace(filter.EntityId))
        {
            queryable = queryable.Where(x => x.EntityId == filter.EntityId);
        }

        return queryable;
    }
}
