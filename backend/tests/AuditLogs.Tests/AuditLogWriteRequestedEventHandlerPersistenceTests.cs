using AuditLogs.Application.Contracts.Auditing;
using AuditLogs.Application.Events;
using AuditLogs.Domain.Auditing;
using AuditLogs.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Shared.EventContracts;
using Xunit;

namespace AuditLogs.Tests;

public sealed class AuditLogWriteRequestedEventHandlerPersistenceTests
{
    [Fact]
    public async Task HandleAsync_persists_the_complete_aggregate_through_the_ambient_consumer_unit_of_work()
    {
        await using var harness = new AuditLogsUnitOfWorkHarness();
        var handler = new AuditLogWriteRequestedEventHandler(
            harness.UnitOfWorkManager,
            harness.CreateRepository<AuditLog>(),
            new AuditLogWriteInfoToEntityConverter(),
            AuditLogTestSignerFactory.CreateDisabled());
        var eventData = CreateEvent();

        await harness.Executor.ExecuteTransactionalAsync(
            cancellationToken => handler.HandleAsync(eventData, cancellationToken));

        await using var verificationContext = harness.CreateDbContext();
        var persistedAuditLog = await verificationContext.AuditLogs
            .IgnoreQueryFilters()
            .Include(x => x.Actions)
            .Include(x => x.EntityChanges)
            .ThenInclude(x => x.PropertyChanges)
            .SingleAsync(x => x.EventId == eventData.EventId);

        var action = Assert.Single(persistedAuditLog.Actions);
        Assert.Equal("IdentityService", action.ServiceName);

        var entityChange = Assert.Single(persistedAuditLog.EntityChanges);
        Assert.Equal("Users.User", entityChange.EntityTypeFullName);
        var propertyChange = Assert.Single(entityChange.PropertyChanges);
        Assert.Equal("Name", propertyChange.PropertyName);
        Assert.Equal("Old", propertyChange.OriginalValue);
        Assert.Equal("New", propertyChange.NewValue);
    }

    private static AuditLogWriteRequestedTenantEto CreateEvent()
    {
        return new AuditLogWriteRequestedTenantEto
        {
            EventId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            TenantId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            OccurredAtUtc = DateTime.SpecifyKind(new DateTime(2026, 3, 6, 20, 0, 0), DateTimeKind.Utc),
            SourceService = "identity",
            Category = "http",
            RedactionApplied = true,
            RedactionVersion = 1,
            Actions =
            [
                new AuditLogActionEto
                {
                    Name = "CreateUser",
                    DisplayName = "IdentityService",
                    DurationMs = 9,
                    Parameters = new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        ["username"] = "jane"
                    }
                }
            ],
            EntityChanges =
            [
                new AuditLogEntityChangeEto
                {
                    ChangeType = "Updated",
                    EntityType = "Users.User",
                    EntityId = "42",
                    PropertyChanges =
                    [
                        new AuditLogEntityPropertyChangeEto
                        {
                            PropertyName = "Name",
                            PropertyType = "System.String",
                            OriginalValue = "Old",
                            NewValue = "New"
                        }
                    ]
                }
            ]
        };
    }
}
