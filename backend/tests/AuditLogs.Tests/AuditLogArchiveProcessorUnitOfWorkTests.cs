using AuditLogs.Application.Auditing;
using AuditLogs.Domain.Auditing;
using AuditLogs.Domain.Shared.Auditing;
using AuditLogs.EntityFrameworkCore.EntityFrameworkCore;
using AuditLogs.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.BlobStoring;
using Shared.Data.Filtering;
using Shared.DistributedLocking.Abstractions;
using Shared.DistributedLocking.Options;
using Shared.MultiTenancy;
using Shared.Uow.Abstractions;
using Xunit;

namespace AuditLogs.Tests;

public sealed class AuditLogArchiveProcessorUnitOfWorkTests
{
    [Fact]
    public async Task Hot_delete_uses_one_transaction_per_keyset_batch_and_held_rows_do_not_block_later_rows()
    {
        await using var harness = new AuditLogsUnitOfWorkHarness();
        var executionTime = new DateTime(2024, 2, 15, 12, 0, 0, DateTimeKind.Utc);
        await using (var seed = harness.CreateDbContext())
        {
            seed.AuditLogs.Add(CreateAuditLog(Guid.NewGuid(), executionTime, "held-user"));
            for (var index = 0; index < 4; index++)
            {
                seed.AuditLogs.Add(CreateAuditLog(Guid.NewGuid(), executionTime.AddMinutes(index + 1), $"free-{index}"));
            }

            seed.Set<AuditLogLegalHold>().Add(
                new AuditLogLegalHold(0, "held subject", null, null, null, "held-user"));
            await seed.SaveChangesAsync();
        }

        var executor = new CountingUnitOfWorkExecutor(harness.Executor);
        var processor = CreateProcessor(harness, executor);

        var deleted = await processor.DeletePartitionRowsAsync(
            new AuditArchivePartition(null, 2024, 2),
            deleteBatchSize: 2,
            CancellationToken.None);

        Assert.Equal(4, deleted);
        Assert.Equal(3, executor.TransactionalCount);
        await using var verification = harness.CreateDbContext();
        var remaining = await verification.AuditLogs.AsNoTracking().Select(x => x.UserId).ToListAsync();
        Assert.Equal(["held-user"], remaining);
    }

    [Fact]
    public async Task Restore_batch_propagates_untranslated_sqlite_unique_constraint()
    {
        await using var harness = new AuditLogsUnitOfWorkHarness();
        var executor = new CountingUnitOfWorkExecutor(harness.Executor);
        var processor = CreateProcessor(harness, executor);
        var eventId = Guid.NewGuid();

        await Assert.ThrowsAsync<DbUpdateException>(() => processor.RestoreBatchAsync(
            [CreateArchivedRecord(eventId, "first"), CreateArchivedRecord(eventId, "duplicate")],
            CancellationToken.None));

        Assert.Equal(1, executor.TransactionalCount);
        await using var verification = harness.CreateDbContext();
        Assert.Equal(0, await verification.AuditLogs.CountAsync(x => x.EventId == eventId));
    }

    [Fact]
    public async Task Restore_batch_rolls_back_rows_inserted_by_duplicate_fallback_when_a_later_row_fails()
    {
        await using var harness = new AuditLogsUnitOfWorkHarness();
        await using (var schema = harness.CreateDbContext())
        {
            await schema.Database.ExecuteSqlRawAsync(
                """
                CREATE TRIGGER FailRestoreRow
                BEFORE INSERT ON AuditLogs
                WHEN NEW.UserId = 'force-failure'
                BEGIN
                    SELECT RAISE(ABORT, 'forced restore failure');
                END;
                """);
        }

        var processor = CreateProcessor(harness);
        var duplicateEventId = Guid.NewGuid();
        var failingEventId = Guid.NewGuid();

        await Assert.ThrowsAsync<DbUpdateException>(() => processor.RestoreBatchAsync(
            [
                CreateArchivedRecord(duplicateEventId, "first"),
                CreateArchivedRecord(duplicateEventId, "duplicate"),
                CreateArchivedRecord(failingEventId, "force-failure")
            ],
            CancellationToken.None));

        await using var verification = harness.CreateDbContext();
        Assert.False(await verification.AuditLogs.AnyAsync(x =>
            x.EventId == duplicateEventId || x.EventId == failingEventId));
    }

    [Fact]
    public async Task New_lock_owner_recovers_interrupted_archive_and_restore_claims()
    {
        await using var harness = new AuditLogsUnitOfWorkHarness();
        long archiveManifestId;
        long restoreManifestId;
        long restoreJobId;
        await using (var seed = harness.CreateDbContext())
        {
            var archiveManifest = new AuditArchiveManifest(0, "host:2024:01", DateTime.UtcNow, DateTime.UtcNow.AddYears(1));
            archiveManifest.MarkArchiving();

            var restoreManifest = new AuditArchiveManifest(0, "host:2024:02", DateTime.UtcNow, DateTime.UtcNow.AddYears(1));
            restoreManifest.MarkArchived("blob://auditlogs-archive/test", 1, "AA", DateTime.UtcNow, DateTime.UtcNow.AddYears(1));
            restoreManifest.MarkRestoreQueued("test");
            restoreManifest.MarkRestoring();
            seed.AuditArchiveManifests.AddRange(archiveManifest, restoreManifest);
            await seed.SaveChangesAsync();

            var restoreJob = new AuditArchiveRestoreJob(
                0,
                restoreManifest.Id,
                restoreManifest.PartitionKey,
                "test",
                "host-admin",
                DateTime.UtcNow,
                Guid.NewGuid().ToString("N"));
            restoreJob.MarkRunning(DateTime.UtcNow);
            seed.AuditArchiveRestoreJobs.Add(restoreJob);
            await seed.SaveChangesAsync();

            archiveManifestId = archiveManifest.Id;
            restoreManifestId = restoreManifest.Id;
            restoreJobId = restoreJob.Id;
        }

        var processor = CreateProcessor(harness);
        await processor.RecoverInterruptedClaimsAsync(CancellationToken.None);

        await using var verification = harness.CreateDbContext();
        Assert.Equal(
            AuditArchiveManifestStatus.PendingArchive,
            (await verification.AuditArchiveManifests.FindAsync(archiveManifestId))!.Status);
        Assert.Equal(
            AuditArchiveManifestStatus.RestoreQueued,
            (await verification.AuditArchiveManifests.FindAsync(restoreManifestId))!.Status);
        var recoveredRestoreJob = (await verification.AuditArchiveRestoreJobs.FindAsync(restoreJobId))!;
        Assert.Equal(AuditArchiveRestoreJobStatus.Queued, recoveredRestoreJob.Status);
        Assert.Null(recoveredRestoreJob.StartedAtUtc);
    }

    [Fact]
    public async Task Lock_contention_skips_maintenance_without_opening_a_unit_of_work()
    {
        await using var harness = new AuditLogsUnitOfWorkHarness();
        var executor = new CountingUnitOfWorkExecutor(harness.Executor);
        var processor = CreateProcessor(
            harness,
            executor,
            distributedLock: new TestDistributedLock(acquired: false),
            options: EnabledArchiveOptions());

        Assert.Equal(0, await processor.ProcessBatchAsync());
        Assert.Equal(0, executor.TransactionalCount);
        Assert.Equal(0, executor.NonTransactionalCount);
    }

    [Fact]
    public async Task Blob_upload_runs_outside_database_transaction()
    {
        await using var harness = new AuditLogsUnitOfWorkHarness();
        await SeedArchiveCandidateAsync(harness);
        var blob = new InspectingBlobContainer(harness.UnitOfWorkManager);
        var processor = CreateProcessor(
            harness,
            blobContainer: blob,
            options: EnabledArchiveOptions());

        Assert.Equal(1, await processor.ProcessBatchAsync());
        Assert.True(blob.SaveObservedOutsideUnitOfWork);
    }

    [Fact]
    public async Task Cancellation_during_blob_upload_propagates_and_leaves_claim_for_next_owner_recovery()
    {
        await using var harness = new AuditLogsUnitOfWorkHarness();
        await SeedArchiveCandidateAsync(harness);
        var blob = new InspectingBlobContainer(harness.UnitOfWorkManager, cancelOnSave: true);
        var processor = CreateProcessor(
            harness,
            blobContainer: blob,
            options: EnabledArchiveOptions());

        await Assert.ThrowsAsync<OperationCanceledException>(() => processor.ProcessBatchAsync());

        await using var verification = harness.CreateDbContext();
        Assert.Equal(
            AuditArchiveManifestStatus.Archiving,
            await verification.AuditArchiveManifests.Select(x => x.Status).SingleAsync());
    }

    private static AuditLogArchiveProcessor CreateProcessor(
        AuditLogsUnitOfWorkHarness harness,
        IUnitOfWorkExecutor? executor = null,
        IDistributedLock? distributedLock = null,
        IBlobContainer<AuditLogArchiveBlobContainer>? blobContainer = null,
        AuditLogArchiveOptions? options = null)
    {
        var currentTenant = harness.Services.GetRequiredService<ICurrentTenant>();
        var dataFilter = harness.Services.GetRequiredService<IDataFilter>();
        var legalHoldService = new AuditLogLegalHoldService(
            harness.DbContextProvider,
            currentTenant,
            dataFilter);

        return new AuditLogArchiveProcessor(
            harness.CreateRepository<AuditArchiveManifest>(),
            harness.CreateRepository<AuditArchiveRestoreJob>(),
            blobContainer ?? new InspectingBlobContainer(harness.UnitOfWorkManager),
            harness.DbContextProvider,
            executor ?? harness.Executor,
            distributedLock ?? new TestDistributedLock(acquired: true),
            currentTenant,
            dataFilter,
            new TestOptionsMonitor<AuditLogArchiveOptions>(options ?? new AuditLogArchiveOptions()),
            legalHoldService,
            NullLogger<AuditLogArchiveProcessor>.Instance);
    }

    private static AuditLogArchiveOptions EnabledArchiveOptions()
    {
        var options = new AuditLogArchiveOptions
        {
            Enabled = true,
            HotRetentionDays = 1,
            MaxPartitionsPerCycle = 1,
            RowBatchSize = 50,
            DeleteArchivedFromHotStore = false,
            AllowRestore = true
        };
        AuditLogArchiveOptionsPostConfigure.Normalize(options);
        return options;
    }

    private static async Task SeedArchiveCandidateAsync(AuditLogsUnitOfWorkHarness harness)
    {
        await using var seed = harness.CreateDbContext();
        seed.AuditLogs.Add(CreateAuditLog(Guid.NewGuid(), DateTime.UtcNow.AddYears(-2), "user-1"));
        await seed.SaveChangesAsync();
    }

    private static AuditLog CreateAuditLog(Guid eventId, DateTime executionTime, string userId)
    {
        return new AuditLog(
            id: 0,
            eventId: eventId,
            applicationName: "identity",
            category: "http",
            tenantId: null,
            tenantName: null,
            userId: userId,
            userName: "Jane",
            executionTime: executionTime,
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

    private static ArchivedAuditLogRecord CreateArchivedRecord(Guid eventId, string userId)
    {
        return new ArchivedAuditLogRecord
        {
            EventId = eventId,
            ApplicationName = "identity",
            Category = "http",
            UserId = userId,
            UserName = "Jane",
            ExecutionTime = DateTime.UtcNow,
            ExecutionDuration = 5,
            HttpMethod = "GET",
            Url = "/api",
            HttpStatusCode = 200
        };
    }

    private sealed class CountingUnitOfWorkExecutor : IUnitOfWorkExecutor
    {
        private readonly IUnitOfWorkExecutor _inner;

        public CountingUnitOfWorkExecutor(IUnitOfWorkExecutor inner)
        {
            _inner = inner;
        }

        public int TransactionalCount { get; private set; }
        public int NonTransactionalCount { get; private set; }

        public Task ExecuteTransactionalAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default)
        {
            TransactionalCount++;
            return _inner.ExecuteTransactionalAsync(operation, cancellationToken);
        }

        public Task<TResult> ExecuteTransactionalAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default)
        {
            TransactionalCount++;
            return _inner.ExecuteTransactionalAsync(operation, cancellationToken);
        }

        public Task ExecuteNonTransactionalAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default)
        {
            NonTransactionalCount++;
            return _inner.ExecuteNonTransactionalAsync(operation, cancellationToken);
        }

        public Task<TResult> ExecuteNonTransactionalAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default)
        {
            NonTransactionalCount++;
            return _inner.ExecuteNonTransactionalAsync(operation, cancellationToken);
        }
    }

    private sealed class TestDistributedLock : IDistributedLock
    {
        private readonly bool _acquired;

        public TestDistributedLock(bool acquired)
        {
            _acquired = acquired;
        }

        public Task<IDistributedLockHandle?> TryAcquireAsync(
            string name,
            DistributedLockAcquireOptions options,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IDistributedLockHandle?>(_acquired ? new TestLockHandle(name) : null);
        }

        public async Task<IDistributedLockHandle> AcquireAsync(
            string name,
            DistributedLockAcquireOptions options,
            CancellationToken cancellationToken = default)
        {
            return await TryAcquireAsync(name, options, cancellationToken)
                   ?? throw new InvalidOperationException("Lock was not acquired.");
        }
    }

    private sealed class TestLockHandle : IDistributedLockHandle
    {
        public TestLockHandle(string name)
        {
            Name = name;
        }

        public string Name { get; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class InspectingBlobContainer : IBlobContainer<AuditLogArchiveBlobContainer>
    {
        private readonly IUnitOfWorkManager _unitOfWorkManager;
        private readonly bool _cancelOnSave;
        private readonly Dictionary<string, byte[]> _blobs = new(StringComparer.Ordinal);

        public InspectingBlobContainer(IUnitOfWorkManager unitOfWorkManager, bool cancelOnSave = false)
        {
            _unitOfWorkManager = unitOfWorkManager;
            _cancelOnSave = cancelOnSave;
        }

        public bool SaveObservedOutsideUnitOfWork { get; private set; }

        public async Task SaveAsync(string name, Stream stream, bool overrideExisting = false, CancellationToken cancellationToken = default)
        {
            SaveObservedOutsideUnitOfWork = _unitOfWorkManager.Current == null;
            if (_cancelOnSave)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            await using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken);
            _blobs[name] = buffer.ToArray();
        }

        public Task<Stream> GetAsync(string name, CancellationToken cancellationToken = default)
            => Task.FromResult<Stream>(new MemoryStream(_blobs[name], writable: false));

        public Task<Stream?> GetOrNullAsync(string name, CancellationToken cancellationToken = default)
            => Task.FromResult<Stream?>(_blobs.TryGetValue(name, out var value) ? new MemoryStream(value, writable: false) : null);

        public Task<bool> ExistsAsync(string name, CancellationToken cancellationToken = default)
            => Task.FromResult(_blobs.ContainsKey(name));

        public Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default)
            => Task.FromResult(_blobs.Remove(name));

        public Task<Uri> GetReadUrlAsync(string name, TimeSpan duration, CancellationToken cancellationToken = default)
            => Task.FromResult(new Uri($"https://example.invalid/{Uri.EscapeDataString(name)}"));
    }
}
