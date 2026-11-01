using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuditArchiveManifestStatus = AuditLogs.Domain.Shared.Auditing.AuditArchiveManifestStatus;
using AuditArchiveRestoreJobStatus = AuditLogs.Domain.Shared.Auditing.AuditArchiveRestoreJobStatus;
using AuditLogSealVerificationRunStatus = AuditLogs.Domain.Shared.Auditing.AuditLogSealVerificationRunStatus;
using AuditLogFilter = AuditLogs.Domain.Shared.Auditing.AuditLogFilter;
using AuditLogExportFormat = AuditLogs.Domain.Shared.Auditing.AuditLogExportFormat;
using AuditLogExportStatus = AuditLogs.Domain.Shared.Auditing.AuditLogExportStatus;
using EntityChangeFilter = AuditLogs.Domain.Shared.Auditing.EntityChangeFilter;
using AuditLogs.Application.Contracts.Auditing;
using AuditLogs.Domain.Auditing;
using AuditLogsErrorCodes = AuditLogs.Domain.Shared.Auditing.AuditLogsErrorCodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shared;
using Shared.AuditLogs.Auditing;
using Shared.CurrentUser;
using Shared.Data.Filtering;
using Shared.Domain;
using Shared.Domain.Repositories;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;

namespace AuditLogs.Application.Auditing;

public sealed partial class AuditLogQueryAppService
{
    private static readonly string[] VerificationFailureIssues = ["Verification failed."];

    public async Task<AuditLogSealVerificationDto> VerifySealRangeAsync(VerifyAuditLogSealRangeInput input)
    {
        EnsureHostArchiveAccess();
        ArgumentNullException.ThrowIfNull(input);

        if (input.ToId < input.FromId)
        {
            throw CreateValidationException(
                AuditLogsErrorCodes.SealRangeInvalid,
                "ToId must be greater than or equal to FromId.");
        }

        var rangeSize = checked(input.ToId - input.FromId + 1);
        var options = _archiveOptions.Value;
        if (rangeSize > options.MaxSealVerificationRows)
        {
            throw CreateValidationException(
                AuditLogsErrorCodes.SealRangeExceeded,
                $"Verification range cannot exceed {options.MaxSealVerificationRows} rows per request.");
        }

        var run = new AuditLogSealVerificationRun(
            id: 0,
            fromId: input.FromId,
            toId: input.ToId,
            requestedByUserId: _currentUser.Id?.ToString("D"),
            requestedAtUtc: DateTime.UtcNow);
        await _sealVerificationRunRepository.InsertAsync(run, autoSave: true);

        if (rangeSize > options.InteractiveSealVerificationMaxRows)
        {
            await _backgroundJobManager.EnqueueAsync(
                new AuditLogSealVerificationJobArgs(run.Id),
                CreateJobOptions(tenantId: null, requestedByUserId: run.RequestedByUserId));
            return MapSealVerification(run);
        }

        run.MarkRunning();
        await _sealVerificationRunRepository.UpdateAsync(run, autoSave: true);
        try
        {
            var result = await _sealService.VerifyRangeAsync(input.FromId, input.ToId, _signer);
            run.MarkCompleted(
                DateTime.UtcNow,
                result.ChainIntact,
                result.SealsChecked,
                result.RowsChecked,
                JsonSerializer.Serialize(result.Issues, JsonOptions));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            run.MarkFailed(
                DateTime.UtcNow,
                JsonSerializer.Serialize(VerificationFailureIssues, JsonOptions));
        }

        await _sealVerificationRunRepository.UpdateAsync(run, autoSave: true);
        return MapSealVerification(run);
    }

    public async Task<PagedResultDto<AuditLogSealVerificationDto>> GetSealVerificationsAsync(
        GetAuditLogSealVerificationsInput input)
    {
        EnsureHostArchiveAccess();
        input ??= new GetAuditLogSealVerificationsInput();
        var maxResultCount = Math.Clamp(input.MaxResultCount, 1, 200);
        var skipCount = Math.Max(0, input.SkipCount);
        var status = NormalizeSealVerificationStatus(input.Status);
        var query = await _sealVerificationRunRepository.GetQueryableAsync();
        if (status.HasValue)
        {
            query = query.Where(x => x.Status == status.Value);
        }

        var totalCount = await query.LongCountAsync();
        var runs = await query
            .OrderByDescending(x => x.RequestedAtUtc)
            .ThenByDescending(x => x.Id)
            .Skip(skipCount)
            .Take(maxResultCount)
            .ToListAsync();
        return new PagedResultDto<AuditLogSealVerificationDto>
        {
            TotalCount = totalCount,
            Items = runs.Select(MapSealVerification).ToList()
        };
    }

    public async Task<AuditLogSealVerificationDto> GetSealVerificationAsync(long id)
    {
        EnsureHostArchiveAccess();
        var run = await _sealVerificationRunRepository.FindAsync(id, includeDetails: false)
            ?? throw CreateNotFoundException(
                AuditLogsErrorCodes.SealVerificationNotFound,
                "Audit seal verification run not found.");
        return MapSealVerification(run);
    }

    public async Task<PagedResultDto<AuditArchiveManifestDto>> GetArchiveManifestsAsync(GetAuditArchiveManifestsInput input)
    {
        EnsureHostArchiveAccess();

        input ??= new GetAuditArchiveManifestsInput();
        var maxResultCount = Math.Clamp(input.MaxResultCount, 1, 200);
        var skipCount = Math.Max(0, input.SkipCount);
        var partitionKeyContains = NormalizeOptional(input.PartitionKeyContains, AuditLogConsts.MaxArchivePartitionKeyLength);
        var status = NormalizeArchiveStatus(input.Status);

        var query = await _archiveManifestRepository.GetQueryableAsync();
        if (!string.IsNullOrWhiteSpace(partitionKeyContains))
        {
            query = query.Where(x => x.PartitionKey.Contains(partitionKeyContains));
        }

        if (status.HasValue)
        {
            query = query.Where(x => x.Status == status.Value);
        }

        var totalCount = await query.LongCountAsync();
        var manifests = await query
            .OrderByDescending(x => x.ArchivedAtUtc)
            .ThenByDescending(x => x.Id)
            .Skip(skipCount)
            .Take(maxResultCount)
            .ToListAsync();

        var items = manifests
            .Select(x => new AuditArchiveManifestDto
            {
                Id = x.Id,
                PartitionKey = x.PartitionKey,
                ArchiveUri = null,
                LocationLabel = BuildArchiveLocationLabel(x.PartitionKey),
                RowCount = x.RowCount,
                Checksum = x.Checksum,
                ArchivedAtUtc = x.ArchivedAtUtc,
                DeleteAfterUtc = x.DeleteAfterUtc,
                Status = x.Status.ToString(),
                LastFailureReason = x.ExtraProperties.TryGetValue("LastFailureReason", out var lastFailureReason)
                    ? lastFailureReason?.ToString()
                    : null,
                LastRestoreQueuedAtUtc = ParseUtcExtraProperty(x.ExtraProperties, "RestoreQueuedAtUtc"),
                LastRestoredAtUtc = ParseUtcExtraProperty(x.ExtraProperties, "LastRestoredAtUtc"),
                LastRestoredRows = ParseLongExtraProperty(x.ExtraProperties, "LastRestoredRows")
            })
            .ToList();

        return new PagedResultDto<AuditArchiveManifestDto>
        {
            TotalCount = totalCount,
            Items = items
        };
    }

    [Audited]
    public async Task<AuditArchiveRestoreJobDto> RequestArchiveRestoreAsync(long id, RequestAuditArchiveRestoreInput input)
    {
        EnsureHostArchiveAccess();

        if (!_archiveOptions.Value.AllowRestore)
        {
            throw CreateConflictException(
                AuditLogsErrorCodes.ArchiveRestoreDisabled,
                "Archive restore is disabled.");
        }

        var manifest = await _archiveManifestRepository.FindAsync(id, includeDetails: false)
            ?? throw CreateNotFoundException(
                AuditLogsErrorCodes.ArchiveManifestNotFound,
                "Audit archive manifest not found.");

        var requestedByUserId = _currentUser.Id?.ToString("D");
        var activeJobs = await _archiveRestoreJobRepository.GetListAsync(
            x =>
                x.ManifestId == id &&
                x.RequestedByUserId == requestedByUserId &&
                (x.Status == AuditArchiveRestoreJobStatus.Queued ||
                 x.Status == AuditArchiveRestoreJobStatus.Running),
            includeDetails: false);
        var activeJob = activeJobs
            .OrderByDescending(x => x.RequestedAtUtc)
            .FirstOrDefault();
        if (activeJob != null)
        {
            return MapRestoreJob(activeJob);
        }

        var reason = NormalizeOptional(input?.Reason, AuditLogConsts.MaxArchiveFailureReasonLength);
        var requestedAtUtc = DateTime.UtcNow;
        var idempotencyKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{manifest.Id}:{requestedByUserId ?? "system"}:{requestedAtUtc:O}")));
        var job = new AuditArchiveRestoreJob(
            id: 0,
            manifestId: manifest.Id,
            partitionKey: manifest.PartitionKey,
            reason: reason,
            requestedByUserId: requestedByUserId,
            requestedAtUtc: requestedAtUtc,
            idempotencyKey: idempotencyKey);
        manifest.MarkRestoreQueued(reason);
        await _archiveRestoreJobRepository.InsertAsync(job, autoSave: true);
        await _archiveManifestRepository.UpdateAsync(manifest, autoSave: true);
        await _backgroundJobManager.EnqueueAsync(
            new AuditLogArchiveMaintenanceJobArgs(job.Id),
            CreateJobOptions(tenantId: null, requestedByUserId: requestedByUserId));

        ArchiveRestoreRequestCounter.Add(1, new KeyValuePair<string, object?>("manifest_id", manifest.Id));
        return MapRestoreJob(job);
    }

    public async Task<PagedResultDto<AuditArchiveRestoreJobDto>> GetArchiveRestoreJobsAsync(
        GetAuditArchiveRestoreJobsInput input)
    {
        EnsureHostArchiveAccess();
        input ??= new GetAuditArchiveRestoreJobsInput();
        var maxResultCount = Math.Clamp(input.MaxResultCount, 1, 200);
        var skipCount = Math.Max(0, input.SkipCount);
        var status = NormalizeRestoreJobStatus(input.Status);
        var query = await _archiveRestoreJobRepository.GetQueryableAsync();
        if (input.ManifestId.HasValue)
        {
            query = query.Where(x => x.ManifestId == input.ManifestId.Value);
        }

        if (status.HasValue)
        {
            query = query.Where(x => x.Status == status.Value);
        }

        var totalCount = await query.LongCountAsync();
        var jobs = await query
            .OrderByDescending(x => x.RequestedAtUtc)
            .ThenByDescending(x => x.Id)
            .Skip(skipCount)
            .Take(maxResultCount)
            .ToListAsync();
        return new PagedResultDto<AuditArchiveRestoreJobDto>
        {
            TotalCount = totalCount,
            Items = jobs.Select(MapRestoreJob).ToList()
        };
    }

    public async Task<AuditArchiveRestoreJobDto> GetArchiveRestoreJobAsync(long id)
    {
        EnsureHostArchiveAccess();
        var job = await _archiveRestoreJobRepository.FindAsync(id, includeDetails: false)
            ?? throw CreateNotFoundException(
                AuditLogsErrorCodes.ArchiveRestoreJobNotFound,
                "Audit archive restore job not found.");
        return MapRestoreJob(job);
    }

    [Audited]
    public async Task<AuditArchiveRestoreJobDto> CancelArchiveRestoreJobAsync(long id)
    {
        EnsureHostArchiveAccess();
        var job = await _archiveRestoreJobRepository.FindAsync(id, includeDetails: false)
            ?? throw CreateNotFoundException(
                AuditLogsErrorCodes.ArchiveRestoreJobNotFound,
                "Audit archive restore job not found.");
        var manifest = await _archiveManifestRepository.FindAsync(job.ManifestId, includeDetails: false)
            ?? throw CreateNotFoundException(
                AuditLogsErrorCodes.ArchiveManifestNotFound,
                "Audit archive manifest not found.");
        job.Cancel(DateTime.UtcNow);
        manifest.CancelRestore();
        await _archiveRestoreJobRepository.UpdateAsync(job, autoSave: true);
        await _archiveManifestRepository.UpdateAsync(manifest, autoSave: true);
        return MapRestoreJob(job);
    }

    [Audited]
    public async Task<AuditArchiveRestoreJobDto> RetryArchiveRestoreJobAsync(long id)
    {
        EnsureHostArchiveAccess();
        var job = await _archiveRestoreJobRepository.FindAsync(id, includeDetails: false)
            ?? throw CreateNotFoundException(
                AuditLogsErrorCodes.ArchiveRestoreJobNotFound,
                "Audit archive restore job not found.");
        var manifest = await _archiveManifestRepository.FindAsync(job.ManifestId, includeDetails: false)
            ?? throw CreateNotFoundException(
                AuditLogsErrorCodes.ArchiveManifestNotFound,
                "Audit archive manifest not found.");
        job.Requeue(DateTime.UtcNow);
        manifest.MarkRestoreQueued(job.Reason);
        await _archiveRestoreJobRepository.UpdateAsync(job, autoSave: true);
        await _archiveManifestRepository.UpdateAsync(manifest, autoSave: true);
        await _backgroundJobManager.EnqueueAsync(
            new AuditLogArchiveMaintenanceJobArgs(job.Id),
            CreateJobOptions(tenantId: null, requestedByUserId: job.RequestedByUserId));
        return MapRestoreJob(job);
    }

    private static string BuildArchiveLocationLabel(string partitionKey)
        => $"Archive partition {partitionKey}";

    private static AuditArchiveRestoreJobStatus? NormalizeRestoreJobStatus(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (Enum.TryParse<AuditArchiveRestoreJobStatus>(value.Trim(), ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        throw CreateValidationException(
            AuditLogsErrorCodes.ArchiveRestoreStatusInvalid,
            "Restore job status filter is invalid.");
    }

    private static AuditLogSealVerificationRunStatus? NormalizeSealVerificationStatus(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (Enum.TryParse<AuditLogSealVerificationRunStatus>(value.Trim(), ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        throw CreateValidationException(
            AuditLogsErrorCodes.SealVerificationStatusInvalid,
            "Seal verification status filter is invalid.");
    }

    private static AuditArchiveRestoreJobDto MapRestoreJob(AuditArchiveRestoreJob job)
        => new()
        {
            Id = job.Id,
            ManifestId = job.ManifestId,
            PartitionKey = job.PartitionKey,
            Status = job.Status.ToString().ToLowerInvariant(),
            Reason = job.Reason,
            RequestedByUserId = job.RequestedByUserId,
            RequestedAtUtc = job.RequestedAtUtc,
            StartedAtUtc = job.StartedAtUtc,
            CompletedAtUtc = job.CompletedAtUtc,
            RestoredRows = job.RestoredRows,
            FailureReason = job.FailureReason,
            IdempotencyKey = job.IdempotencyKey
        };

    private static AuditLogSealVerificationDto MapSealVerification(AuditLogSealVerificationRun run)
    {
        List<string> issues;
        try
        {
            issues = JsonSerializer.Deserialize<List<string>>(run.IssuesJson, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            issues = ["Verification result could not be read."];
        }

        return new AuditLogSealVerificationDto
        {
            Id = run.Id,
            FromId = run.FromId,
            ToId = run.ToId,
            Status = run.Status.ToString().ToLowerInvariant(),
            ChainIntact = run.ChainIntact,
            SealsChecked = run.SealsChecked,
            RowsChecked = run.RowsChecked,
            Issues = issues,
            RequestedAtUtc = run.RequestedAtUtc,
            CompletedAtUtc = run.CompletedAtUtc
        };
    }

    internal async Task<IReadOnlyList<AuditLogExportRow>> LoadExportRowsAsync(
        AuditLogExportFilterSnapshot snapshot,
        int skipCount,
        int maxResultCount,
        CancellationToken cancellationToken)
    {
        return await ExecuteWithHostMultiTenantFilterDisabledAsync(async () =>
        {
            var filter = new AuditLogFilter
            {
                TenantId = snapshot.TenantId,
                FromUtc = snapshot.FromUtc,
                ToUtc = snapshot.ToUtc,
                UserId = snapshot.UserId,
                ApplicationName = snapshot.ApplicationName,
                Category = snapshot.Category,
                UrlContains = snapshot.UrlContains,
                HttpStatusCode = snapshot.HttpStatusCode
            };

            var query = await _queryStore.GetListQueryableAsync(
                filter,
                includeDetails: false);

            return await query
                .OrderByDescending(x => x.ExecutionTime)
                .ThenByDescending(x => x.Id)
                .Skip(skipCount)
                .Take(maxResultCount)
                .Select(x => new AuditLogExportRow
                {
                    Id = x.Id,
                    EventId = x.EventId,
                    TenantId = x.TenantId,
                    ExecutionTime = x.ExecutionTime,
                    ExecutionDuration = x.ExecutionDuration,
                    ApplicationName = x.ApplicationName,
                    Category = x.Category,
                    UserId = x.UserId,
                    UserName = x.UserName,
                    HttpMethod = x.HttpMethod,
                    Url = x.Url,
                    HttpStatusCode = x.HttpStatusCode,
                    CorrelationId = x.CorrelationId,
                    TraceId = x.TraceId,
                    ClientId = x.ClientId
                })
                .ToListAsync(cancellationToken);
        });
    }
}
