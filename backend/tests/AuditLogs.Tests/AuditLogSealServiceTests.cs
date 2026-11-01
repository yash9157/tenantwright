using AuditLogs.Application.Auditing;
using AuditLogs.Domain.Auditing;
using AuditLogs.EntityFrameworkCore.EntityFrameworkCore;
using AuditLogs.Tests.TestDoubles;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.AuditLogs.Publishing;
using Xunit;

namespace AuditLogs.Tests;

/// <summary>
/// Sub-flow 1b: periodic sealing chain with a lag watermark. Uses a real SQLite-backed
/// <c>AuditLogsDbContextApp</c> because the sealing logic relies on relational ordering/CreationTime
/// comparisons not faithfully reproduced by the EF Core InMemory provider.
/// </summary>
public class AuditLogSealServiceTests : IDisposable
{
    private const string KeyId = "seal-key";
    private readonly SqliteConnection _connection;
    private readonly AuditLogsDbContextApp _dbContext;

    public AuditLogSealServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbContext = AuditLogsEfCoreTestHarness.BuildSqliteDbContext(_connection);
        _dbContext.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task SealNextBatchAsync_should_seal_rows_older_than_the_watermark()
    {
        await InsertAuditLogAsync(id: 1, creationTime: DateTime.UtcNow.AddMinutes(-5));
        await InsertAuditLogAsync(id: 2, creationTime: DateTime.UtcNow.AddMinutes(-5));

        var service = CreateSealService(watermarkLagSeconds: 30);
        var sealed1 = await service.SealNextBatchAsync();

        Assert.True(sealed1);
        var seal = await _dbContext.Set<AuditLogSeal>().SingleAsync();
        Assert.Equal(1, seal.FromId);
        Assert.Equal(2, seal.ToId);
        Assert.Equal(2, seal.RowCount);
        Assert.Equal(AuditLogs.Domain.Auditing.AuditLogSealConsts.GenesisHash, seal.PrevSealHash);
    }

    [Fact]
    public async Task SealNextBatchAsync_should_persist_the_seal_through_a_single_transactional_operation()
    {
        await InsertAuditLogAsync(id: 1, creationTime: DateTime.UtcNow.AddMinutes(-5));
        await InsertAuditLogAsync(id: 2, creationTime: DateTime.UtcNow.AddMinutes(-5));

        var recorder = new RecordingUnitOfWorkExecutor();
        var service = CreateSealService(watermarkLagSeconds: 30, executor: recorder);

        var sealed1 = await service.SealNextBatchAsync();

        // Row loading, hashing, and signing use one bounded non-transactional unit of work; only the
        // append-only seal insertion uses a fresh transactional operation.
        Assert.True(sealed1);
        Assert.Equal(1, recorder.TransactionalCount);
        Assert.Equal(1, recorder.NonTransactionalCount);
        Assert.Single(await _dbContext.Set<AuditLogSeal>().ToListAsync());
    }

    [Fact]
    public async Task SealNextBatchAsync_should_not_seal_rows_inside_the_watermark_window()
    {
        // Row just committed "now" - well inside a 120s watermark window.
        await InsertAuditLogAsync(id: 1, creationTime: DateTime.UtcNow);

        var service = CreateSealService(watermarkLagSeconds: 120);
        var sealed1 = await service.SealNextBatchAsync();

        Assert.False(sealed1);
        Assert.Empty(await _dbContext.Set<AuditLogSeal>().ToListAsync());
    }

    [Fact]
    public async Task SealNextBatchAsync_should_not_seal_a_gap_when_no_later_row_is_old_enough_yet()
    {
        // Row 2 (higher Id) exists but is still inside the watermark window - too young to prove
        // row 1's absence is a permanent gap rather than an in-flight commit. Nothing should seal yet.
        await InsertAuditLogAsync(id: 2, creationTime: DateTime.UtcNow);

        var service = CreateSealService(watermarkLagSeconds: 120);
        var sealedResult = await service.SealNextBatchAsync();

        Assert.False(sealedResult);
        Assert.Empty(await _dbContext.Set<AuditLogSeal>().ToListAsync());
    }

    [Fact]
    public async Task SealNextBatchAsync_should_skip_a_gap_once_a_later_row_ages_past_the_watermark()
    {
        // Row 1 never arrives (its transaction rolled back - a permanent identity-sequence gap).
        // Sequence values are assigned in increasing order at statement-execution time, so once row 2
        // (assigned after row 1) has aged past the watermark without row 1 ever showing up, row 1 is
        // provably abandoned - safe to skip rather than block sealing forever.
        await InsertAuditLogAsync(id: 2, creationTime: DateTime.UtcNow.AddMinutes(-5));

        var service = CreateSealService(watermarkLagSeconds: 30);
        var sealedResult = await service.SealNextBatchAsync();

        Assert.True(sealedResult);
        var seal = await _dbContext.Set<AuditLogSeal>().SingleAsync();
        Assert.Equal(1, seal.FromId);
        Assert.Equal(2, seal.ToId);
        Assert.Equal(1, seal.RowCount);
    }

    [Fact]
    public async Task SealNextBatchAsync_should_include_a_late_committing_lower_id_row_in_the_next_seal_if_it_arrives_first()
    {
        // Row 2 is still too young to seal past (no later proof of abandonment yet).
        await InsertAuditLogAsync(id: 2, creationTime: DateTime.UtcNow);
        var service = CreateSealService(watermarkLagSeconds: 120);
        Assert.False(await service.SealNextBatchAsync());

        // Row 1 arrives late (a slow transaction that finally committed) before the watermark forces a
        // decision - it must be included in the seal like any normal row, not treated as a gap.
        await InsertAuditLogAsync(id: 1, creationTime: DateTime.UtcNow.AddMinutes(-5));

        var sealedResult = await service.SealNextBatchAsync();

        Assert.True(sealedResult);
        var seal = await _dbContext.Set<AuditLogSeal>().SingleAsync();
        Assert.Equal(1, seal.FromId);
        Assert.Equal(1, seal.ToId);
        Assert.Equal(1, seal.RowCount);
    }

    [Fact]
    public async Task VerifyRangeAsync_should_report_chain_intact_for_untampered_seals()
    {
        await InsertAuditLogAsync(id: 1, creationTime: DateTime.UtcNow.AddMinutes(-5));
        await InsertAuditLogAsync(id: 2, creationTime: DateTime.UtcNow.AddMinutes(-5));

        var service = CreateSealService(watermarkLagSeconds: 30);
        await service.SealNextBatchAsync();

        var signer = AuditLogTestSignerFactory.CreateWithKey(KeyId, AuditLogTestSignerFactory.CreateTestKeyBase64());
        var result = await service.VerifyRangeAsync(1, 2, signer);

        Assert.True(result.ChainIntact);
        Assert.Empty(result.Issues);
        Assert.Equal(1, result.SealsChecked);
        Assert.Equal(2, result.RowsChecked);
    }

    [Fact]
    public async Task VerifyRangeAsync_should_detect_a_deleted_row_within_a_sealed_range()
    {
        await InsertAuditLogAsync(id: 1, creationTime: DateTime.UtcNow.AddMinutes(-5));
        await InsertAuditLogAsync(id: 2, creationTime: DateTime.UtcNow.AddMinutes(-5));

        var service = CreateSealService(watermarkLagSeconds: 30);
        await service.SealNextBatchAsync();

        // Simulate deletion of row 2 after sealing.
        var row2 = await _dbContext.AuditLogs.SingleAsync(x => x.Id == 2);
        _dbContext.AuditLogs.Remove(row2);
        await _dbContext.SaveChangesAsync();

        var signer = AuditLogTestSignerFactory.CreateWithKey(KeyId, AuditLogTestSignerFactory.CreateTestKeyBase64());
        var result = await service.VerifyRangeAsync(1, 2, signer);

        Assert.False(result.ChainIntact);
        Assert.Contains(result.Issues, issue => issue.Contains("Row count mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public async Task VerifyRangeAsync_should_detect_a_broken_chain_link_between_two_seals()
    {
        await InsertAuditLogAsync(id: 1, creationTime: DateTime.UtcNow.AddMinutes(-5));
        var service = CreateSealService(watermarkLagSeconds: 30, maxBatchSize: 1);
        await service.SealNextBatchAsync();

        await InsertAuditLogAsync(id: 2, creationTime: DateTime.UtcNow.AddMinutes(-5));
        await service.SealNextBatchAsync();

        // Tamper with the first seal's Signature after the fact - simulates an attacker altering the
        // seal row directly (they'd need DB access, which is exactly what sealing across an insider
        // threat model tries to catch downstream via anchoring - here we just prove local detection).
        var seals = await _dbContext.Set<AuditLogSeal>().OrderBy(x => x.FromId).ToListAsync();
        Assert.Equal(2, seals.Count);

        var firstSealEntity = await _dbContext.Set<AuditLogSeal>().FindAsync(seals[0].Id);
        _dbContext.Entry(firstSealEntity!).Property(nameof(AuditLogSeal.Signature)).CurrentValue = "0000";
        await _dbContext.SaveChangesAsync();

        var signer = AuditLogTestSignerFactory.CreateWithKey(KeyId, AuditLogTestSignerFactory.CreateTestKeyBase64());
        var result = await service.VerifyRangeAsync(1, 2, signer);

        Assert.False(result.ChainIntact);
        Assert.Contains(result.Issues, issue => issue.Contains("chain broken", StringComparison.OrdinalIgnoreCase));
    }

    private AuditLogSealService CreateSealService(
        int watermarkLagSeconds,
        int maxBatchSize = 5000,
        RecordingUnitOfWorkExecutor? executor = null)
    {
        return new AuditLogSealService(
            new StaticDbContextProvider<AuditLogsDbContextApp>(_dbContext),
            executor ?? new RecordingUnitOfWorkExecutor(),
            new TestCurrentTenant(null),
            new ConfigurableDataFilter(false),
            new TestOptionsMonitor<AuditLogSealOptions>(new AuditLogSealOptions
            {
                Enabled = true,
                WatermarkLagSeconds = watermarkLagSeconds,
                MaxBatchSize = maxBatchSize
            }),
            new TestOptionsMonitor<AuditLogSignatureOptions>(
                AuditLogTestSignerFactory.CreateSignatureOptions(KeyId, AuditLogTestSignerFactory.CreateTestKeyBase64())),
            NullLogger<AuditLogSealService>.Instance);
    }

    private async Task InsertAuditLogAsync(long id, DateTime creationTime)
    {
        var auditLog = new AuditLog(
            id: 0,
            eventId: Guid.NewGuid(),
            applicationName: "identity",
            category: "http",
            tenantId: null,
            tenantName: null,
            userId: "user-1",
            userName: "Jane",
            executionTime: creationTime,
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

        // Force the Id and CreationTime to specific test values (identity column + audit property
        // setter would otherwise assign both automatically) via raw SQL, since the domain entity has no
        // public setter for either and SQLite's identity-by-default sequence would otherwise just use
        // insertion order.
        await _dbContext.Database.ExecuteSqlRawAsync(
            "UPDATE \"AuditLogs\" SET \"Id\" = {0}, \"CreationTime\" = {1} WHERE \"EventId\" = {2}",
            id,
            creationTime,
            auditLog.EventId);
        _dbContext.ChangeTracker.Clear();
    }
}
