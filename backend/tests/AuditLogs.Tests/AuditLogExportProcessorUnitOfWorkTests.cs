using AuditLogs.Application.Auditing;
using AuditLogs.Domain.Auditing;
using AuditLogs.Domain.Shared.Auditing;
using AuditLogs.Tests.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.Uow.Abstractions;
using Xunit;

namespace AuditLogs.Tests;

public sealed class AuditLogExportProcessorUnitOfWorkTests
{
    [Fact]
    public async Task ProcessJobAsync_success_persists_claim_reads_and_completion_with_real_uow_provider_composition()
    {
        await using var harness = new AuditLogsUnitOfWorkHarness();
        await InsertAuditLogAsync(harness);
        await InsertAuditLogAsync(harness);
        var exportId = await InsertQueuedExportAsync(harness, "{\"maxRowCount\":1000}");
        var processor = CreateProcessor(harness, harness.Executor);

        await processor.ProcessJobAsync(exportId);

        await using var verification = harness.CreateDbContext();
        var export = await verification.AuditLogExports.AsNoTracking().SingleAsync(x => x.Id == exportId);
        Assert.Equal(AuditLogExportStatus.Completed, export.Status);
        Assert.Equal(2, export.RowCount);
        Assert.NotNull(export.FileContent);
    }

    [Fact]
    public async Task ProcessJobAsync_generation_failure_records_failure_after_the_failed_phase_unwinds()
    {
        await using var harness = new AuditLogsUnitOfWorkHarness();
        var exportId = await InsertQueuedExportAsync(harness, "this-is-not-valid-json");
        var processor = CreateProcessor(harness, harness.Executor);

        await Assert.ThrowsAnyAsync<Exception>(() => processor.ProcessJobAsync(exportId));

        await using var verification = harness.CreateDbContext();
        var export = await verification.AuditLogExports.AsNoTracking().SingleAsync(x => x.Id == exportId);
        Assert.Equal(AuditLogExportStatus.Failed, export.Status);
        Assert.NotNull(export.FailureReason);
    }

    [Fact]
    public async Task ProcessJobAsync_failure_recording_error_fails_the_job_instead_of_returning_with_unresolved_claim()
    {
        await using var harness = new AuditLogsUnitOfWorkHarness();
        var exportId = await InsertQueuedExportAsync(harness, "this-is-not-valid-json");
        var executor = new FailOnTransactionalCallExecutor(harness.Executor, failingCall: 2);
        var processor = CreateProcessor(harness, executor);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => processor.ProcessJobAsync(exportId));
        Assert.Equal("failure-state transaction unavailable", exception.Message);

        await using var verification = harness.CreateDbContext();
        Assert.Equal(
            AuditLogExportStatus.Processing,
            await verification.AuditLogExports.Where(x => x.Id == exportId).Select(x => x.Status).SingleAsync());
    }

    [Fact]
    public async Task ProcessJobAsync_cancellation_during_generation_propagates_without_terminal_failure()
    {
        await using var harness = new AuditLogsUnitOfWorkHarness();
        await InsertAuditLogAsync(harness);
        var exportId = await InsertQueuedExportAsync(harness, "{\"maxRowCount\":1000}");
        var processor = CreateProcessor(harness, new CancelOnReadExecutor(harness.Executor));

        await Assert.ThrowsAsync<OperationCanceledException>(() => processor.ProcessJobAsync(exportId));

        await using var verification = harness.CreateDbContext();
        Assert.Equal(
            AuditLogExportStatus.Processing,
            await verification.AuditLogExports.Where(x => x.Id == exportId).Select(x => x.Status).SingleAsync());
    }

    private static AuditLogExportProcessor CreateProcessor(
        AuditLogsUnitOfWorkHarness harness,
        IUnitOfWorkExecutor executor)
    {
        return new AuditLogExportProcessor(
            harness.CreateRepository<AuditLogExport>(),
            harness.DbContextProvider,
            executor,
            new TestCurrentTenant(null),
            new ConfigurableDataFilter(false),
            NullLogger<AuditLogExportProcessor>.Instance);
    }

    private static async Task<long> InsertQueuedExportAsync(
        AuditLogsUnitOfWorkHarness harness,
        string filterSnapshotJson)
    {
        await using var dbContext = harness.CreateDbContext();
        var export = new AuditLogExport(
            id: 0,
            tenantId: null,
            requestedByUserId: "user-1",
            format: AuditLogExportFormat.Csv,
            filterSnapshotJson: filterSnapshotJson,
            maxRowCount: 1000,
            chunkSize: 100,
            requestedAtUtc: DateTime.UtcNow,
            expireAtUtc: DateTime.UtcNow.AddDays(1));

        dbContext.AuditLogExports.Add(export);
        await dbContext.SaveChangesAsync();
        return export.Id;
    }

    private static async Task InsertAuditLogAsync(AuditLogsUnitOfWorkHarness harness)
    {
        await using var dbContext = harness.CreateDbContext();
        dbContext.AuditLogs.Add(new AuditLog(
            id: 0,
            eventId: Guid.NewGuid(),
            applicationName: "identity",
            category: "http",
            tenantId: null,
            tenantName: null,
            userId: "user-1",
            userName: "Jane",
            executionTime: DateTime.UtcNow.AddMinutes(-1),
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
            comments: null));
        await dbContext.SaveChangesAsync();
    }

    private sealed class CancelOnReadExecutor : IUnitOfWorkExecutor
    {
        private readonly IUnitOfWorkExecutor _inner;

        public CancelOnReadExecutor(IUnitOfWorkExecutor inner)
        {
            _inner = inner;
        }

        public Task ExecuteTransactionalAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default)
            => _inner.ExecuteTransactionalAsync(operation, cancellationToken);

        public Task<TResult> ExecuteTransactionalAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default)
            => _inner.ExecuteTransactionalAsync(operation, cancellationToken);

        public Task ExecuteNonTransactionalAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default)
            => throw new OperationCanceledException(cancellationToken);

        public Task<TResult> ExecuteNonTransactionalAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default)
            => throw new OperationCanceledException(cancellationToken);
    }

    private sealed class FailOnTransactionalCallExecutor : IUnitOfWorkExecutor
    {
        private readonly IUnitOfWorkExecutor _inner;
        private readonly int _failingCall;
        private int _transactionalCalls;

        public FailOnTransactionalCallExecutor(IUnitOfWorkExecutor inner, int failingCall)
        {
            _inner = inner;
            _failingCall = failingCall;
        }

        public Task ExecuteTransactionalAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default)
        {
            ThrowOnConfiguredCall();
            return _inner.ExecuteTransactionalAsync(operation, cancellationToken);
        }

        public Task<TResult> ExecuteTransactionalAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default)
        {
            ThrowOnConfiguredCall();
            return _inner.ExecuteTransactionalAsync(operation, cancellationToken);
        }

        public Task ExecuteNonTransactionalAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default)
            => _inner.ExecuteNonTransactionalAsync(operation, cancellationToken);

        public Task<TResult> ExecuteNonTransactionalAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default)
            => _inner.ExecuteNonTransactionalAsync(operation, cancellationToken);

        private void ThrowOnConfiguredCall()
        {
            _transactionalCalls++;
            if (_transactionalCalls == _failingCall)
            {
                throw new InvalidOperationException("failure-state transaction unavailable");
            }
        }
    }
}
