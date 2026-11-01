using AuditLogs.Application.Auditing;
using AuditLogs.Domain.Auditing;
using Xunit;

namespace AuditLogs.Tests;

public class AuditLogExportProcessorKeysetTests
{
    [Fact]
    public void ApplyKeysetCursor_should_return_only_rows_after_cursor_in_desc_order()
    {
        var t = DateTime.SpecifyKind(new DateTime(2026, 2, 17, 9, 0, 0), DateTimeKind.Utc);
        var rows = new[]
        {
            CreateAuditLog(10, t),
            CreateAuditLog(9, t),
            CreateAuditLog(8, t.AddMinutes(-1)),
            CreateAuditLog(7, t.AddMinutes(-2))
        }.AsQueryable();

        var filtered = AuditLogExportProcessor.ApplyKeysetCursor(rows, new AuditLogExportCursor(t, 9))
            .OrderByDescending(x => x.ExecutionTime)
            .ThenByDescending(x => x.Id)
            .Select(x => x.Id)
            .ToList();

        Assert.Equal([8L, 7L], filtered);
    }

    [Fact]
    public void ApplyKeysetCursor_should_keep_query_unchanged_when_cursor_missing()
    {
        var t = DateTime.SpecifyKind(new DateTime(2026, 2, 17, 9, 0, 0), DateTimeKind.Utc);
        var rows = new[]
        {
            CreateAuditLog(3, t),
            CreateAuditLog(2, t.AddMinutes(-1)),
            CreateAuditLog(1, t.AddMinutes(-2))
        }.AsQueryable();

        var filtered = AuditLogExportProcessor.ApplyKeysetCursor(rows, null)
            .OrderByDescending(x => x.ExecutionTime)
            .ThenByDescending(x => x.Id)
            .Select(x => x.Id)
            .ToList();

        Assert.Equal([3L, 2L, 1L], filtered);
    }

    private static AuditLog CreateAuditLog(long id, DateTime executionTime)
    {
        return new AuditLog(
            id: id,
            eventId: Guid.NewGuid(),
            applicationName: "audit",
            category: "http",
            tenantId: null,
            tenantName: null,
            userId: null,
            userName: null,
            executionTime: executionTime,
            executionDuration: 1,
            clientIpAddress: null,
            clientName: null,
            clientId: null,
            correlationId: null,
            traceId: null,
            browserInfo: null,
            httpMethod: "GET",
            url: "/",
            httpStatusCode: 200,
            impersonatorUserId: null,
            impersonatorUserName: null,
            impersonatorTenantId: null,
            impersonatorTenantName: null,
            exceptions: null,
            comments: null);
    }
}
