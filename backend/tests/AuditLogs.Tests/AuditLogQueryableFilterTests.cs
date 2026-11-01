using AuditLogs.Domain.Auditing;
using AuditLogs.Domain.Shared.Auditing;
using AuditLogs.EntityFrameworkCore.EntityFrameworkCore;
using Xunit;

namespace AuditLogs.Tests;

public class AuditLogQueryableFilterTests
{
    [Fact]
    public void ApplyFilter_should_filter_audit_logs_by_tenant_user_and_status()
    {
        var tenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var tenantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var baseTime = DateTime.SpecifyKind(new DateTime(2026, 2, 16, 10, 0, 0), DateTimeKind.Utc);
        var logs = new[]
        {
            CreateAuditLog(1, Guid.NewGuid(), tenantA, "user-1", "identity", "http", "/api/users", 200, baseTime),
            CreateAuditLog(2, Guid.NewGuid(), tenantA, "user-2", "identity", "http", "/api/users", 500, baseTime.AddMinutes(1)),
            CreateAuditLog(3, Guid.NewGuid(), tenantB, "user-1", "tenant", "http", "/api/tenant", 200, baseTime.AddMinutes(2))
        }.AsQueryable();

        var filtered = logs.ApplyFilter(new AuditLogFilter
        {
            TenantId = tenantA,
            UserId = "user-1",
            HttpStatusCode = 200
        });

        var results = filtered.ToList();
        Assert.Single(results);
        Assert.Equal(1, results[0].Id);
    }

    [Fact]
    public void ApplyFilter_should_filter_audit_logs_by_client_ip_correlation_and_trace_id()
    {
        var baseTime = DateTime.SpecifyKind(new DateTime(2026, 2, 16, 10, 0, 0), DateTimeKind.Utc);
        var logs = new[]
        {
            CreateAuditLogWithTracing(1, "10.0.0.1", "corr-1", "trace-1", baseTime),
            CreateAuditLogWithTracing(2, "10.0.0.2", "corr-2", "trace-2", baseTime.AddMinutes(1)),
            CreateAuditLogWithTracing(3, "10.0.0.1", "corr-1", "trace-3", baseTime.AddMinutes(2))
        }.AsQueryable();

        var byIp = logs.ApplyFilter(new AuditLogFilter { ClientIpAddress = "10.0.0.1" }).ToList();
        Assert.Equal(2, byIp.Count);
        Assert.All(byIp, x => Assert.Equal("10.0.0.1", x.ClientIpAddress));

        var byCorrelation = logs.ApplyFilter(new AuditLogFilter { CorrelationId = "corr-2" }).ToList();
        Assert.Single(byCorrelation);
        Assert.Equal(2, byCorrelation[0].Id);

        var byTrace = logs.ApplyFilter(new AuditLogFilter { TraceId = "trace-3" }).ToList();
        Assert.Single(byTrace);
        Assert.Equal(3, byTrace[0].Id);

        var byAll = logs.ApplyFilter(new AuditLogFilter
        {
            ClientIpAddress = "10.0.0.1",
            CorrelationId = "corr-1",
            TraceId = "trace-1"
        }).ToList();
        Assert.Single(byAll);
        Assert.Equal(1, byAll[0].Id);
    }

    [Fact]
    public void ApplyFilter_should_filter_entity_changes_by_entity_and_time_range()
    {
        var tenant = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var baseTime = DateTime.SpecifyKind(new DateTime(2026, 2, 16, 10, 0, 0), DateTimeKind.Utc);
        var changes = new[]
        {
            CreateEntityChange(1, 100, tenant, baseTime, "Updated", "Users.User", "42"),
            CreateEntityChange(2, 101, tenant, baseTime.AddMinutes(5), "Updated", "Users.User", "42"),
            CreateEntityChange(3, 102, tenant, baseTime.AddMinutes(10), "Updated", "Users.User", "99")
        }.AsQueryable();

        var filtered = changes.ApplyFilter(new EntityChangeFilter
        {
            TenantId = tenant,
            EntityTypeFullName = "Users.User",
            EntityId = "42",
            FromUtc = baseTime.AddMinutes(1),
            ToUtc = baseTime.AddMinutes(6)
        });

        var results = filtered.ToList();
        Assert.Single(results);
        Assert.Equal(2, results[0].Id);
    }

    private static AuditLog CreateAuditLogWithTracing(
        long id,
        string clientIpAddress,
        string correlationId,
        string traceId,
        DateTime executionTime)
    {
        return new AuditLog(
            id: id,
            eventId: Guid.NewGuid(),
            applicationName: "identity",
            category: "http",
            tenantId: null,
            tenantName: null,
            userId: "user-1",
            userName: "name",
            executionTime: executionTime,
            executionDuration: 5,
            clientIpAddress: clientIpAddress,
            clientName: null,
            clientId: null,
            correlationId: correlationId,
            traceId: traceId,
            browserInfo: null,
            httpMethod: "GET",
            url: "/api",
            httpStatusCode: 200,
            impersonatorUserId: null,
            impersonatorUserName: null,
            impersonatorTenantId: null,
            impersonatorTenantName: null,
            exceptions: null,
            comments: null);
    }

    private static AuditLog CreateAuditLog(
        long id,
        Guid eventId,
        Guid? tenantId,
        string? userId,
        string applicationName,
        string category,
        string url,
        int httpStatusCode,
        DateTime executionTime)
    {
        return new AuditLog(
            id: id,
            eventId: eventId,
            applicationName: applicationName,
            category: category,
            tenantId: tenantId,
            tenantName: "tenant",
            userId: userId,
            userName: "name",
            executionTime: executionTime,
            executionDuration: 10,
            clientIpAddress: null,
            clientName: null,
            clientId: null,
            correlationId: null,
            traceId: null,
            browserInfo: null,
            httpMethod: "GET",
            url: url,
            httpStatusCode: httpStatusCode,
            impersonatorUserId: null,
            impersonatorUserName: null,
            impersonatorTenantId: null,
            impersonatorTenantName: null,
            exceptions: null,
            comments: null);
    }

    private static EntityChange CreateEntityChange(
        long id,
        long auditLogId,
        Guid? tenantId,
        DateTime changeTime,
        string changeType,
        string entityTypeFullName,
        string entityId)
    {
        return new EntityChange(
            id: id,
            auditLogId: auditLogId,
            tenantId: tenantId,
            changeTime: changeTime,
            changeType: changeType,
            entityTenantId: tenantId,
            entityId: entityId,
            entityTypeFullName: entityTypeFullName);
    }
}
