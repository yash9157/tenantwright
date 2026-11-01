namespace AuditLogs.Application.Contracts.Auditing;

public interface IAuditLogQueryAppService
{
    Task<PagedResultDto<AuditLogSummaryDto>> GetListAsync(GetAuditLogsInput input);

    /// <summary>
    /// One audit log by id. Audit log ids are unique per database, so <paramref name="tenantId"/> (the row's
    /// <c>TenantId</c> from the list) selects that tenant's database, its own when dedicated; without it the shared
    /// database is read (host events and tenants on the shared database).
    /// </summary>
    Task<AuditLogDetailDto> GetAsync(long id, Guid? tenantId = null);

    /// <inheritdoc cref="GetAsync"/>
    Task<AuditLogSensitivePayloadDto> GetSensitivePayloadAsync(long id, Guid? tenantId = null);

    Task<PagedResultDto<AuditLogEntityChangeDto>> GetEntityChangesAsync(GetAuditLogEntityChangesInput input);

    Task<AuditLogEntityChangeDetailDto> GetEntityChangeAsync(long id, Guid tenantId);

    Task<AuditLogExportJobDto> CreateExportAsync(CreateAuditLogExportInput input);

    Task<PagedResultDto<AuditLogExportJobDto>> GetExportsAsync(GetAuditLogExportsInput input);

    Task<AuditLogExportJobDto> GetExportAsync(long id);

    Task<AuditLogExportJobDto> CancelExportAsync(long id);

    Task<AuditLogExportJobDto> RetryExportAsync(long id);

    Task<AuditLogExportDownloadTokenDto> IssueExportDownloadTokenAsync(long id);

    Task<AuditLogExportDownloadDto> GetExportDownloadAsync(long id, string token);

    Task<PagedResultDto<AuditArchiveManifestDto>> GetArchiveManifestsAsync(GetAuditArchiveManifestsInput input);

    Task<AuditArchiveRestoreJobDto> RequestArchiveRestoreAsync(long id, RequestAuditArchiveRestoreInput input);

    Task<PagedResultDto<AuditArchiveRestoreJobDto>> GetArchiveRestoreJobsAsync(GetAuditArchiveRestoreJobsInput input);

    Task<AuditArchiveRestoreJobDto> GetArchiveRestoreJobAsync(long id);

    Task<AuditArchiveRestoreJobDto> CancelArchiveRestoreJobAsync(long id);

    Task<AuditArchiveRestoreJobDto> RetryArchiveRestoreJobAsync(long id);

    Task<AuditLogSealVerificationDto> VerifySealRangeAsync(VerifyAuditLogSealRangeInput input);

    Task<PagedResultDto<AuditLogSealVerificationDto>> GetSealVerificationsAsync(GetAuditLogSealVerificationsInput input);

    Task<AuditLogSealVerificationDto> GetSealVerificationAsync(long id);

    Task<AuditLogSummaryReportDto> GetSummaryReportAsync(GetAuditLogSummaryInput input);
}
