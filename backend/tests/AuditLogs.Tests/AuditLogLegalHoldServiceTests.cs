using AuditLogs.Application.Auditing;
using AuditLogs.Domain.Auditing;
using AuditLogs.EntityFrameworkCore.EntityFrameworkCore;
using AuditLogs.Tests.TestDoubles;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AuditLogs.Tests;

/// <summary>
/// Sub-flow 5: legal hold row-filtering used by the archive hot-store delete path.
/// </summary>
public class AuditLogLegalHoldServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AuditLogsDbContextApp _dbContext;
    private readonly AuditLogLegalHoldService _service;

    public AuditLogLegalHoldServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbContext = AuditLogsEfCoreTestHarness.BuildSqliteDbContext(_connection);
        _dbContext.Database.EnsureCreated();
        _service = new AuditLogLegalHoldService(
            new StaticDbContextProvider<AuditLogsDbContextApp>(_dbContext),
            new TestCurrentTenant(null),
            new ConfigurableDataFilter(false));
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task FilterOutHeldRowsAsync_should_return_all_ids_unchanged_when_no_holds_exist()
    {
        var row1 = await InsertAuditLogAsync(tenantId: null, userId: "user-1");
        var row2 = await InsertAuditLogAsync(tenantId: null, userId: "user-2");

        var result = await _service.FilterOutHeldRowsAsync([row1, row2]);

        Assert.Equal([row1, row2], result.OrderBy(x => x).ToList());
    }

    [Fact]
    public async Task FilterOutHeldRowsAsync_should_exclude_rows_covered_by_an_active_hold()
    {
        var tenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var tenantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var heldRow = await InsertAuditLogAsync(tenantId: tenantA, userId: "user-1");
        var freeRow = await InsertAuditLogAsync(tenantId: tenantB, userId: "user-2");

        var hold = new AuditLogLegalHold(0, "tenant-a-hold", tenantA, null, null, null);
        _dbContext.Set<AuditLogLegalHold>().Add(hold);
        await _dbContext.SaveChangesAsync();

        var result = await _service.FilterOutHeldRowsAsync([heldRow, freeRow]);

        Assert.Equal([freeRow], result);
    }

    [Fact]
    public async Task FilterOutHeldRowsAsync_should_not_exclude_rows_once_the_hold_is_released()
    {
        var tenantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var row = await InsertAuditLogAsync(tenantId: tenantA, userId: "user-1");

        var hold = new AuditLogLegalHold(0, "tenant-a-hold", tenantA, null, null, null);
        _dbContext.Set<AuditLogLegalHold>().Add(hold);
        await _dbContext.SaveChangesAsync();

        Assert.Empty(await _service.FilterOutHeldRowsAsync([row]));

        hold.Release("case closed");
        await _dbContext.SaveChangesAsync();

        var result = await _service.FilterOutHeldRowsAsync([row]);
        Assert.Equal([row], result);
    }

    [Fact]
    public async Task HasAnyActiveHoldAsync_should_reflect_active_and_released_holds()
    {
        Assert.False(await _service.HasAnyActiveHoldAsync());

        var hold = new AuditLogLegalHold(0, "hold", Guid.NewGuid(), null, null, null);
        _dbContext.Set<AuditLogLegalHold>().Add(hold);
        await _dbContext.SaveChangesAsync();

        Assert.True(await _service.HasAnyActiveHoldAsync());

        hold.Release(null);
        await _dbContext.SaveChangesAsync();

        Assert.False(await _service.HasAnyActiveHoldAsync());
    }

    private async Task<long> InsertAuditLogAsync(Guid? tenantId, string userId)
    {
        var auditLog = new AuditLog(
            id: 0,
            eventId: Guid.NewGuid(),
            applicationName: "identity",
            category: "http",
            tenantId: tenantId,
            tenantName: null,
            userId: userId,
            userName: "name",
            executionTime: DateTime.UtcNow,
            executionDuration: 5,
            clientIpAddress: null,
            clientName: null,
            clientId: null,
            correlationId: null,
            traceId: null,
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

        _dbContext.AuditLogs.Add(auditLog);
        await _dbContext.SaveChangesAsync();
        return auditLog.Id;
    }
}
