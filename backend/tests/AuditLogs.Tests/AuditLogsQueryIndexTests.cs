using AuditLogs.Domain.Auditing;
using AuditLogs.EntityFrameworkCore.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace AuditLogs.Tests;

/// <summary>
/// Holds the audit console's filters and cursors together with the indexes behind them.
///
/// Two gaps this pins closed. The entity-changes cursor pages on <c>(ChangeTime, Id)</c> within a tenant,
/// exactly as the main log pages on <c>(ExecutionTime, Id)</c> — but the main log had a tenant-scoped
/// composite for it and entity changes had only a tenant-less <c>ChangeTime</c> index, leaving the more
/// selective of the two cursors the less covered. And <c>Category</c> and <c>ApplicationName</c> are both
/// offered as filters with nothing indexed behind either, so filtering by one scanned the tenant's whole log.
///
/// These assertions prove the indexes are <em>declared</em>. Whether PostgreSQL's planner chooses them needs
/// EXPLAIN against representative volume, which is recorded as outstanding rather than asserted here.
/// </summary>
public sealed class AuditLogsQueryIndexTests
{
    private static AuditLogsDbContextApp CreateContext() =>
        new(
            new DbContextOptionsBuilder<AuditLogsDbContextApp>()
                .UseSqlite("Data Source=:memory:")
                .Options);

    private static bool HasColumns(IIndex index, params string[] columns) =>
        index.Properties.Select(property => property.Name).SequenceEqual(columns);

    [Fact]
    public void Entity_change_cursor_is_as_covered_as_the_audit_log_cursor()
    {
        using var context = CreateContext();
        var entityChange = context.Model.FindEntityType(typeof(EntityChange))!;
        var auditLog = context.Model.FindEntityType(typeof(AuditLog))!;

        Assert.Single(
            entityChange.GetIndexes(),
            index => HasColumns(index, "TenantId", "ChangeTime", "Id"));
        // The pairing this mirrors. Asserted so the two cursors cannot drift apart again.
        Assert.Single(
            auditLog.GetIndexes(),
            index => HasColumns(index, "TenantId", "ExecutionTime", "Id"));
    }

    [Fact]
    public void Entity_type_lookup_index_survives_beside_the_cursor_index()
    {
        // The drill-down from a record to its change history reads these columns, not the cursor's.
        using var context = CreateContext();
        var entityChange = context.Model.FindEntityType(typeof(EntityChange))!;

        Assert.Single(
            entityChange.GetIndexes(),
            index => HasColumns(index, "TenantId", "EntityTypeFullName", "EntityId"));
    }

    [Theory]
    [InlineData("Category")]
    [InlineData("ApplicationName")]
    public void Offered_audit_filters_are_indexed_with_the_list_ordering(string filterColumn)
    {
        // ExecutionTime is the third column because the list always orders by it, so one index serves the
        // filter and the sort rather than needing a separate sort step per page.
        using var context = CreateContext();
        var auditLog = context.Model.FindEntityType(typeof(AuditLog))!;

        Assert.Single(
            auditLog.GetIndexes(),
            index => HasColumns(index, "TenantId", filterColumn, "ExecutionTime"));
    }
}
