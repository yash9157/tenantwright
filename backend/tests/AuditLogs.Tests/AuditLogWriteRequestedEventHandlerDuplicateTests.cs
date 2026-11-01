using AuditLogs.Application.Contracts.Auditing;
using AuditLogs.Application.Events;
using AuditLogs.Domain.Auditing;
using AuditLogs.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Shared.EventContracts;
using Xunit;

namespace AuditLogs.Tests;

public sealed class AuditLogWriteRequestedEventHandlerDuplicateTests
{
    [Fact]
    public async Task Redelivered_event_is_acknowledged_without_persisting_a_second_audit_log()
    {
        await using var harness = new AuditLogsUnitOfWorkHarness(dataFiltersEnabled: true);
        var handler = new AuditLogWriteRequestedEventHandler(
            harness.UnitOfWorkManager,
            harness.CreateRepository<AuditLog>(),
            new AuditLogWriteInfoToEntityConverter(),
            AuditLogTestSignerFactory.CreateDisabled());
        var eventData = new AuditLogWriteRequestedTenantEto
        {
            EventId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            TenantId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            OccurredAtUtc = DateTime.SpecifyKind(new DateTime(2026, 3, 6, 20, 0, 0), DateTimeKind.Utc),
            SourceService = "identity",
            Category = "http",
            RedactionApplied = true,
            RedactionVersion = 1
        };

        await harness.Executor.ExecuteTransactionalAsync(
            cancellationToken => handler.HandleAsync(eventData, cancellationToken));
        await harness.Executor.ExecuteTransactionalAsync(
            cancellationToken => handler.HandleAsync(eventData, cancellationToken));

        await using var verificationContext = harness.CreateDbContext();
        var visibleThroughHostTenantFilterCount = await verificationContext.AuditLogs
            .CountAsync(x => x.EventId == eventData.EventId);
        var persistedCount = await verificationContext.AuditLogs
            .IgnoreQueryFilters()
            .CountAsync(x => x.EventId == eventData.EventId);

        Assert.Equal(0, visibleThroughHostTenantFilterCount);
        Assert.Equal(1, persistedCount);
    }
}
