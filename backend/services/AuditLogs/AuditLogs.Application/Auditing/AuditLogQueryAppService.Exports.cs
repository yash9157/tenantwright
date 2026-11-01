using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuditArchiveManifestStatus = AuditLogs.Domain.Shared.Auditing.AuditArchiveManifestStatus;
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
    [Audited]
    public async Task<AuditLogExportJobDto> CreateExportAsync(CreateAuditLogExportInput input)
    {
        EnsureHostOperatorAccess();
        input ??= new CreateAuditLogExportInput();

        var snapshot = NormalizeExportSnapshot(input);
        var format = NormalizeExportFormat(input.Format);
        var now = DateTime.UtcNow;
        var requestedByUserId = _currentUser.Id?.ToString("D");

        var export = new AuditLogExport(
            id: 0,
            tenantId: snapshot.TenantId,
            requestedByUserId: requestedByUserId,
            format: format,
            filterSnapshotJson: JsonSerializer.Serialize(snapshot, JsonOptions),
            maxRowCount: snapshot.MaxRowCount,
            chunkSize: ExportChunkSize,
            requestedAtUtc: now,
            expireAtUtc: now.AddHours(ExportTtlHours));

        await _exportRepository.InsertAsync(export, autoSave: true);
        await _backgroundJobManager.EnqueueAsync(
            new AuditLogExportJobArgs(export.Id, export.TenantId),
            CreateJobOptions(export.TenantId, requestedByUserId));

        ExportRequestCounter.Add(1);
        return MapExport(export, downloadToken: null);
    }

    public async Task<AuditLogExportJobDto> GetExportAsync(long id)
    {
        return await ExecuteWithHostMultiTenantFilterDisabledAsync(async () =>
        {
            var export = await _exportRepository.FindAsync(id, includeDetails: false);
            if (export == null)
            {
                throw CreateNotFoundException(AuditLogsErrorCodes.AuditExportNotFound, "Audit export not found.");
            }

            EnsureExportAccess(export);

            if (export.Status == AuditLogExportStatus.Completed && DateTime.UtcNow >= export.ExpireAtUtc)
            {
                export.MarkExpired(DateTime.UtcNow);
                await _exportRepository.UpdateAsync(export, autoSave: true);
            }

            return MapExport(export, downloadToken: null);
        });
    }

    public async Task<PagedResultDto<AuditLogExportJobDto>> GetExportsAsync(GetAuditLogExportsInput input)
    {
        input ??= new GetAuditLogExportsInput();
        var maxResultCount = Math.Clamp(input.MaxResultCount, 1, 200);
        var skipCount = Math.Max(0, input.SkipCount);
        var status = NormalizeExportStatus(input.Status);
        var fromUtc = NormalizeUtc(input.RequestedFromUtc);
        var toUtc = NormalizeUtc(input.RequestedToUtc);
        var tenantFilter = ResolveTenantFilter(input.TenantId);

        return await ExecuteWithHostMultiTenantFilterDisabledAsync(async () =>
        {
            var query = await _exportRepository.GetQueryableAsync();

            if (tenantFilter.HasValue)
            {
                query = query.Where(x => x.TenantId == tenantFilter.Value);
            }

            if (status.HasValue)
            {
                query = query.Where(x => x.Status == status.Value);
            }

            if (fromUtc.HasValue)
            {
                query = query.Where(x => x.RequestedAtUtc >= fromUtc.Value);
            }

            if (toUtc.HasValue)
            {
                query = query.Where(x => x.RequestedAtUtc <= toUtc.Value);
            }

            var totalCount = await query.LongCountAsync();
            var exports = await query
                .OrderByDescending(x => x.RequestedAtUtc)
                .ThenByDescending(x => x.Id)
                .Skip(skipCount)
                .Take(maxResultCount)
                .ToListAsync();

            return new PagedResultDto<AuditLogExportJobDto>
            {
                TotalCount = totalCount,
                Items = exports.Select(x => MapExport(x, downloadToken: null)).ToList()
            };
        });
    }

    public async Task<AuditLogExportDownloadTokenDto> IssueExportDownloadTokenAsync(long id)
    {
        return await ExecuteWithHostMultiTenantFilterDisabledAsync(async () =>
        {
            var export = await _exportRepository.FindAsync(id, includeDetails: false)
                ?? throw CreateNotFoundException(AuditLogsErrorCodes.AuditExportNotFound, "Audit export not found.");

            EnsureExportAccess(export, "cross_tenant_token_issue");
            EnsureExportRequesterAccess(export);

            var now = DateTime.UtcNow;
            if (now >= export.ExpireAtUtc)
            {
                if (export.Status == AuditLogExportStatus.Completed)
                {
                    export.MarkExpired(now);
                    await _exportRepository.UpdateAsync(export, autoSave: true);
                }

                throw CreateConflictException(AuditLogsErrorCodes.ExportExpired, "Audit export has expired.");
            }

            if (export.Status != AuditLogExportStatus.Completed ||
                export.FileContent == null ||
                string.IsNullOrWhiteSpace(export.FileName) ||
                string.IsNullOrWhiteSpace(export.ContentType))
            {
                throw CreateConflictException(AuditLogsErrorCodes.ExportNotReady, "Audit export is not ready for download.");
            }

            var token = CreateDownloadToken();
            var expiresAtUtc = now.Add(ExportDownloadTokenLifetime);
            if (expiresAtUtc > export.ExpireAtUtc)
            {
                expiresAtUtc = export.ExpireAtUtc;
            }

            export.ReplaceDownloadToken(ComputeTokenHash(token), expiresAtUtc);
            await _exportRepository.UpdateAsync(export, autoSave: true);

            return new AuditLogExportDownloadTokenDto
            {
                DownloadToken = token,
                ExpiresAtUtc = expiresAtUtc
            };
        });
    }

    [Audited]
    public async Task<AuditLogExportJobDto> CancelExportAsync(long id)
    {
        return await ExecuteWithHostMultiTenantFilterDisabledAsync(async () =>
        {
            var export = await _exportRepository.FindAsync(id, includeDetails: false)
                ?? throw CreateNotFoundException(AuditLogsErrorCodes.AuditExportNotFound, "Audit export not found.");

            EnsureExportAccess(export);
            export.MarkCancelled(DateTime.UtcNow);
            await _exportRepository.UpdateAsync(export, autoSave: true);
            return MapExport(export, downloadToken: null);
        });
    }

    [Audited]
    public async Task<AuditLogExportJobDto> RetryExportAsync(long id)
    {
        return await ExecuteWithHostMultiTenantFilterDisabledAsync(async () =>
        {
            var export = await _exportRepository.FindAsync(id, includeDetails: false)
                ?? throw CreateNotFoundException(AuditLogsErrorCodes.AuditExportNotFound, "Audit export not found.");

            EnsureExportAccess(export);
            export.RequeueForRetry(DateTime.UtcNow);
            await _exportRepository.UpdateAsync(export, autoSave: true);
            await _backgroundJobManager.EnqueueAsync(
                new AuditLogExportJobArgs(export.Id, export.TenantId),
                CreateJobOptions(export.TenantId, export.RequestedByUserId));

            return MapExport(export, downloadToken: null);
        });
    }

    private static AuditLogExportStatus? NormalizeExportStatus(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (Enum.TryParse<AuditLogExportStatus>(value.Trim(), ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        throw CreateValidationException(AuditLogsErrorCodes.InvalidExportStatusFilter, "Export status filter is invalid.");
    }

    public async Task<AuditLogExportDownloadDto> GetExportDownloadAsync(long id, string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            ExportAccessDeniedCounter.Add(1, new KeyValuePair<string, object?>("reason", "missing_token"));
            throw CreateValidationException(AuditLogsErrorCodes.DownloadTokenRequired, "Download token is required.");
        }

        return await ExecuteWithHostMultiTenantFilterDisabledAsync(async () =>
        {
            var started = Stopwatch.GetTimestamp();
            var export = await _exportRepository.FindAsync(id, includeDetails: false);
            if (export == null)
            {
                ExportAccessDeniedCounter.Add(1, new KeyValuePair<string, object?>("reason", "not_found"));
                throw CreateNotFoundException(AuditLogsErrorCodes.AuditExportNotFound, "Audit export not found.");
            }

            EnsureExportAccess(export, "cross_tenant_download");
            EnsureExportRequesterAccess(export);

            var now = DateTime.UtcNow;
            if (now >= export.ExpireAtUtc)
            {
                ExportAccessDeniedCounter.Add(1, new KeyValuePair<string, object?>("reason", "expired"));
                throw CreateConflictException(AuditLogsErrorCodes.ExportExpired, "Audit export has expired.");
            }

            if (export.Status != AuditLogExportStatus.Completed ||
                export.FileContent == null ||
                string.IsNullOrWhiteSpace(export.FileName) ||
                string.IsNullOrWhiteSpace(export.ContentType))
            {
                ExportAccessDeniedCounter.Add(1, new KeyValuePair<string, object?>("reason", "not_ready"));
                throw CreateConflictException(AuditLogsErrorCodes.ExportNotReady, "Audit export is not ready for download.");
            }

            var tokenHash = export.DownloadTokenHash;
            var tokenExpiresAtUtc = export.DownloadTokenExpiresAtUtc;
            if (string.IsNullOrWhiteSpace(tokenHash) ||
                !tokenExpiresAtUtc.HasValue ||
                now >= tokenExpiresAtUtc.Value ||
                !IsTokenValid(tokenHash, token))
            {
                ExportAccessDeniedCounter.Add(1, new KeyValuePair<string, object?>("reason", "invalid_token"));
                throw new UnauthorizedAccessException("Invalid download token.");
            }

            var content = export.FileContentCompressed
                ? Decompress(export.FileContent)
                : export.FileContent;

            export.ConsumeDownloadToken(now);
            await _exportRepository.UpdateAsync(export, autoSave: true);

            ExportDownloadCounter.Add(1);
            ExportDownloadDurationMs.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);

            return new AuditLogExportDownloadDto
            {
                FileName = export.FileName,
                ContentType = export.ContentType,
                Content = content
            };
        });
    }

}
