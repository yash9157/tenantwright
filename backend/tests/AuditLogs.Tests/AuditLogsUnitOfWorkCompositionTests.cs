using AuditLogs.Domain.Auditing;
using AuditLogs.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AuditLogs.Tests;

public sealed class AuditLogsUnitOfWorkCompositionTests
{
    [Fact]
    public async Task DbContext_provider_is_requested_only_after_executor_enters_ambient_scope()
    {
        await using var harness = new AuditLogsUnitOfWorkHarness();

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.DbContextProvider.GetDbContextAsync());

        await harness.Executor.ExecuteNonTransactionalAsync(async _ =>
        {
            Assert.NotNull(harness.UnitOfWorkManager.Current);
            var dbContext = await harness.DbContextProvider.GetDbContextAsync();
            Assert.NotNull(dbContext);
        });

        Assert.Null(harness.UnitOfWorkManager.Current);
    }

    [Fact]
    public async Task Failed_transactional_operation_rolls_back_explicit_save_with_real_provider_composition()
    {
        await using var harness = new AuditLogsUnitOfWorkHarness();
        var eventId = Guid.NewGuid();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Executor.ExecuteTransactionalAsync(async ct =>
            {
                var dbContext = await harness.DbContextProvider.GetDbContextAsync();
                dbContext.AuditLogs.Add(CreateAuditLog(eventId));
                await dbContext.SaveChangesAsync(ct);
                throw new InvalidOperationException("force rollback");
            }));

        await using var verificationContext = harness.CreateDbContext();
        Assert.False(await verificationContext.AuditLogs.AnyAsync(x => x.EventId == eventId));
    }

    private static AuditLog CreateAuditLog(Guid eventId)
    {
        return new AuditLog(
            id: 0,
            eventId: eventId,
            applicationName: "identity",
            category: "http",
            tenantId: null,
            tenantName: null,
            userId: "user-1",
            userName: "Jane",
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
    }
}
