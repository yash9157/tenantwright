using System.Text.Json;
using AuditLogs.Domain.Auditing;
using AuditLogs.Domain.Shared.Auditing;
using Microsoft.Extensions.Logging;
using Shared.BackgroundTasks.Jobs;
using Shared.AuditLogs.Publishing;
using Shared.Domain.Repositories;
using Shared.Uow.Attributes;

namespace AuditLogs.Application.Auditing;

/// <summary>
/// Verifies a requested Id range of the shared database's seal chain. Shared-database-only by design: verification
/// runs are host-operator records enqueued without a tenant, and Id ranges are only meaningful within one database's
/// own sequence. Chains in dedicated tenant databases are written by <c>AuditLogSealWorker</c>; verifying them needs a
/// run that names its tenant database, which this host operator surface does not offer.
/// </summary>
internal sealed class AuditLogSealVerificationBackgroundJob : IBackgroundJob<AuditLogSealVerificationJobArgs>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] VerificationFailureIssues = ["Verification failed."];
    private readonly IRepository<AuditLogSealVerificationRun, long> _repository;
    private readonly AuditLogSealService _sealService;
    private readonly AuditLogEventSigner _signer;
    private readonly ILogger<AuditLogSealVerificationBackgroundJob> _logger;

    public AuditLogSealVerificationBackgroundJob(
        IRepository<AuditLogSealVerificationRun, long> repository,
        AuditLogSealService sealService,
        AuditLogEventSigner signer,
        ILogger<AuditLogSealVerificationBackgroundJob> logger)
    {
        _repository = repository;
        _sealService = sealService;
        _signer = signer;
        _logger = logger;
    }

    // Non-transactional whole-job scope: the run is marked Running, verified with read-only queries,
    // and settled at the end. Verification may span many rows/seals and must not hold a database
    // transaction; the Running and terminal state writes commit as independent statements.
    [UnitOfWork(isTransactional: false)]
    public async Task ExecuteAsync(BackgroundJobExecutionContext<AuditLogSealVerificationJobArgs> context)
    {
        var run = await _repository.FindAsync(
            context.Args.VerificationRunId,
            includeDetails: false,
            cancellationToken: context.CancellationToken);
        if (run == null || run.Status != AuditLogSealVerificationRunStatus.Queued)
        {
            return;
        }

        run.MarkRunning();
        await _repository.UpdateAsync(run, autoSave: true, cancellationToken: context.CancellationToken);

        try
        {
            var result = await _sealService.VerifyRangeAsync(
                run.FromId,
                run.ToId,
                _signer,
                context.CancellationToken);
            run.MarkCompleted(
                DateTime.UtcNow,
                result.ChainIntact,
                result.SealsChecked,
                result.RowsChecked,
                JsonSerializer.Serialize(result.Issues, JsonOptions));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                "Audit seal verification run {VerificationRunId} failed. ErrorType={ErrorType}",
                run.Id,
                ex.GetType().Name);
            run.MarkFailed(DateTime.UtcNow, JsonSerializer.Serialize(VerificationFailureIssues, JsonOptions));
        }

        await _repository.UpdateAsync(run, autoSave: true, cancellationToken: context.CancellationToken);
    }
}
