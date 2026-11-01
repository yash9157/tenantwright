using AuditLogs.Application.Auditing;
using AuditLogs.Domain.Auditing;
using AuditLogs.EntityFrameworkCore.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shared.Domain.Repositories;

namespace AuditLogs.Tests.TestDoubles;

/// <summary>
/// Builds a real <see cref="AuditLogSealService"/> over a fresh in-memory SQLite
/// <c>AuditLogsDbContextApp</c>, for tests that construct <c>AuditLogQueryAppService</c> directly (which
/// takes a concrete, non-interface <see cref="AuditLogSealService"/> dependency) but don't exercise
/// sealing behavior themselves. Callers that DO want to exercise sealing should keep the returned
/// connection alive and use the same context/repository directly instead.
/// </summary>
internal static class AuditLogTestSealServiceFactory
{
    public static AuditLogSealService CreateDisabled()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var dbContext = AuditLogsEfCoreTestHarness.BuildSqliteDbContext(connection);
        dbContext.Database.EnsureCreated();

        return new AuditLogSealService(
            new StaticDbContextProvider<AuditLogsDbContextApp>(dbContext),
            new RecordingUnitOfWorkExecutor(),
            new TestCurrentTenant(null),
            new ConfigurableDataFilter(false),
            new TestOptionsMonitor<AuditLogSealOptions>(new AuditLogSealOptions { Enabled = false }),
            new TestOptionsMonitor<Shared.AuditLogs.Publishing.AuditLogSignatureOptions>(
                new Shared.AuditLogs.Publishing.AuditLogSignatureOptions()),
            NullLogger<AuditLogSealService>.Instance);
    }
}
