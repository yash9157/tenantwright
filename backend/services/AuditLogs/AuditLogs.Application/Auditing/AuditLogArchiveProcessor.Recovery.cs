using AuditLogs.Domain.Auditing;
using AuditLogs.Domain.Shared.Auditing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AuditLogs.Application.Auditing;

public sealed partial class AuditLogArchiveProcessor
{
    internal async Task RecoverInterruptedClaimsAsync(CancellationToken cancellationToken)
    {
        var recoveredManifests = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var recovered = await _uowExecutor.ExecuteTransactionalAsync(async ct =>
            {
                var dbContext = await _dbContextProvider.GetDbContextAsync();
                var manifests = await dbContext.AuditArchiveManifests
                    .Where(x =>
                        x.Status == AuditArchiveManifestStatus.Archiving ||
                        x.Status == AuditArchiveManifestStatus.Restoring)
                    .OrderBy(x => x.Id)
                    .Take(RecoveryBatchSize)
                    .ToListAsync(ct);

                foreach (var manifest in manifests)
                {
                    if (manifest.Status == AuditArchiveManifestStatus.Archiving)
                    {
                        manifest.RecoverInterruptedArchive();
                    }
                    else
                    {
                        manifest.RecoverInterruptedRestore();
                    }
                }

                return manifests.Count;
            }, cancellationToken);

            recoveredManifests += recovered;
            if (recovered < RecoveryBatchSize)
            {
                break;
            }
        }

        var recoveredJobs = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var recovered = await _uowExecutor.ExecuteTransactionalAsync(async ct =>
            {
                var dbContext = await _dbContextProvider.GetDbContextAsync();
                var jobs = await dbContext.AuditArchiveRestoreJobs
                    .Where(x => x.Status == AuditArchiveRestoreJobStatus.Running)
                    .OrderBy(x => x.Id)
                    .Take(RecoveryBatchSize)
                    .ToListAsync(ct);
                var recoveredAtUtc = DateTime.UtcNow;
                foreach (var job in jobs)
                {
                    job.RecoverInterrupted(recoveredAtUtc);
                }

                return jobs.Count;
            }, cancellationToken);

            recoveredJobs += recovered;
            if (recovered < RecoveryBatchSize)
            {
                break;
            }
        }

        if (recoveredManifests > 0 || recoveredJobs > 0)
        {
            _logger.LogWarning(
                "Recovered {ManifestCount} interrupted archive manifest claim(s) and {RestoreJobCount} restore job claim(s).",
                recoveredManifests,
                recoveredJobs);
        }
    }
}
