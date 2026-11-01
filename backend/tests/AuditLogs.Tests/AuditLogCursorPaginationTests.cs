using AuditLogs.Application.Auditing;
using AuditLogs.Domain.Auditing;
using Shared;
using Xunit;

namespace AuditLogs.Tests;

public class AuditLogCursorPaginationTests
{
    [Fact]
    public void Audit_log_cursor_should_continue_after_timestamp_and_id()
    {
        var time = DateTime.SpecifyKind(new DateTime(2026, 6, 14, 7, 0, 0), DateTimeKind.Utc);
        var rows = new[]
        {
            CreateAuditLog(10, time),
            CreateAuditLog(9, time),
            CreateAuditLog(8, time.AddMinutes(-1))
        }.AsQueryable();
        var cursor = AuditLogCursorCodec.Encode(time, 9);

        var result = AuditLogCursorCodec.ApplyAuditLogCursor(rows, cursor)
            .OrderByDescending(row => row.ExecutionTime)
            .ThenByDescending(row => row.Id)
            .Select(row => row.Id)
            .ToList();

        Assert.Equal([8L], result);
    }

    [Fact]
    public void Entity_change_cursor_should_continue_after_timestamp_and_id()
    {
        var time = DateTime.SpecifyKind(new DateTime(2026, 6, 14, 7, 0, 0), DateTimeKind.Utc);
        var rows = new[]
        {
            CreateEntityChange(10, time),
            CreateEntityChange(9, time),
            CreateEntityChange(8, time.AddMinutes(-1))
        }.AsQueryable();
        var cursor = AuditLogCursorCodec.Encode(time, 9);

        var result = AuditLogCursorCodec.ApplyEntityChangeCursor(rows, cursor)
            .OrderByDescending(row => row.ChangeTime)
            .ThenByDescending(row => row.Id)
            .Select(row => row.Id)
            .ToList();

        Assert.Equal([8L], result);
    }

    [Fact]
    public void Invalid_cursor_should_fail_closed()
    {
        Assert.Throws<SharedValidationException>(() => AuditLogCursorCodec.Decode("not-a-cursor"));
    }

    private static AuditLog CreateAuditLog(long id, DateTime executionTime)
    {
        return new AuditLog(
            id,
            Guid.NewGuid(),
            "audit",
            "http",
            null,
            null,
            null,
            null,
            executionTime,
            1,
            null,
            null,
            null,
            null,
            null,
            null,
            "GET",
            "/",
            200,
            null,
            null,
            null,
            null,
            null,
            null);
    }

    private static EntityChange CreateEntityChange(long id, DateTime changeTime)
    {
        return new EntityChange(
            id,
            auditLogId: id,
            tenantId: null,
            changeTime,
            "Updated",
            entityTenantId: null,
            entityId: id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            entityTypeFullName: "Test.Entity");
    }
}
