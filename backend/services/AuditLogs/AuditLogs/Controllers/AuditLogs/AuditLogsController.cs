using AuditLogs.Application.Contracts.Auditing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shared.AuditLogs.Auditing;
using Shared.Permissions.Definitions.AuditLogs;

namespace AuditLogs.Controllers.AuditLogs;

[ApiController]
[Route("api/audit-logs")]
[Authorize]
public class AuditLogsController : ControllerBase
{
    private readonly IAuditLogQueryAppService _queryAppService;
    private readonly IAuditLogLegalHoldAppService _legalHoldAppService;

    public AuditLogsController(
        IAuditLogQueryAppService queryAppService,
        IAuditLogLegalHoldAppService legalHoldAppService)
    {
        _queryAppService = queryAppService;
        _legalHoldAppService = legalHoldAppService;
    }

    [HttpGet]
    [Authorize(Policy = AuditLogsPermissions.Read)]
    public Task<PagedResultDto<AuditLogSummaryDto>> GetListAsync([FromQuery] GetAuditLogsInput input)
    {
        return _queryAppService.GetListAsync(input);
    }

    [HttpGet("{id:long}")]
    [Authorize(Policy = AuditLogsPermissions.ReadDetails)]
    public Task<AuditLogDetailDto> GetAsync(long id, [FromQuery] Guid? tenantId = null)
    {
        return _queryAppService.GetAsync(id, tenantId);
    }

    [HttpGet("{id:long}/sensitive-payload")]
    [Authorize(Policy = AuditLogsPermissions.SensitivePayload.Read)]
    public Task<AuditLogSensitivePayloadDto> GetSensitivePayloadAsync(long id, [FromQuery] Guid? tenantId = null)
    {
        return _queryAppService.GetSensitivePayloadAsync(id, tenantId);
    }

    [HttpGet("entity-changes")]
    [Authorize(Policy = AuditLogsPermissions.EntityChanges.Read)]
    public Task<PagedResultDto<AuditLogEntityChangeDto>> GetEntityChangesAsync(
        [FromQuery] GetAuditLogEntityChangesInput input)
    {
        return _queryAppService.GetEntityChangesAsync(input);
    }

    [HttpGet("entity-changes/{id:long}")]
    [Authorize(Policy = AuditLogsPermissions.EntityChanges.Read)]
    public Task<AuditLogEntityChangeDetailDto> GetEntityChangeAsync(long id, [FromQuery] Guid tenantId)
    {
        return _queryAppService.GetEntityChangeAsync(id, tenantId);
    }

    [HttpPost("exports")]
    [Authorize(Policy = AuditLogsPermissions.Export)]
    [Audited]
    public Task<AuditLogExportJobDto> CreateExportAsync([FromBody] CreateAuditLogExportInput input)
    {
        return _queryAppService.CreateExportAsync(input);
    }

    [HttpGet("exports")]
    [Authorize(Policy = AuditLogsPermissions.Export)]
    public Task<PagedResultDto<AuditLogExportJobDto>> GetExportsAsync([FromQuery] GetAuditLogExportsInput input)
    {
        return _queryAppService.GetExportsAsync(input);
    }

    [HttpGet("exports/{id:long}")]
    [Authorize(Policy = AuditLogsPermissions.Export)]
    public Task<AuditLogExportJobDto> GetExportAsync(long id)
    {
        return _queryAppService.GetExportAsync(id);
    }

    [HttpPost("exports/{id:long}/cancel")]
    [Authorize(Policy = AuditLogsPermissions.Export)]
    [Audited]
    public Task<AuditLogExportJobDto> CancelExportAsync(long id)
    {
        return _queryAppService.CancelExportAsync(id);
    }

    [HttpPost("exports/{id:long}/retry")]
    [Authorize(Policy = AuditLogsPermissions.Export)]
    [Audited]
    public Task<AuditLogExportJobDto> RetryExportAsync(long id)
    {
        return _queryAppService.RetryExportAsync(id);
    }

    [HttpPost("exports/{id:long}/download-token")]
    [Authorize(Policy = AuditLogsPermissions.Export)]
    [Audited]
    public Task<AuditLogExportDownloadTokenDto> IssueExportDownloadTokenAsync(long id)
    {
        return _queryAppService.IssueExportDownloadTokenAsync(id);
    }

    [HttpGet("exports/{id:long}/download")]
    [Authorize(Policy = AuditLogsPermissions.Export)]
    public async Task<IActionResult> DownloadExportAsync(
        long id,
        [FromHeader(Name = "X-Audit-Download-Token")] string token)
    {
        var export = await _queryAppService.GetExportDownloadAsync(id, token);
        return File(export.Content, export.ContentType, export.FileName);
    }

    [HttpGet("archive/manifests")]
    [Authorize(Policy = AuditLogsPermissions.Archive.Read)]
    public Task<PagedResultDto<AuditArchiveManifestDto>> GetArchiveManifestsAsync(
        [FromQuery] GetAuditArchiveManifestsInput input)
    {
        return _queryAppService.GetArchiveManifestsAsync(input);
    }

    [HttpPost("archive/manifests/{id:long}/restore")]
    [Authorize(Policy = AuditLogsPermissions.Archive.Restore)]
    [Audited]
    public Task<AuditArchiveRestoreJobDto> RequestArchiveRestoreAsync(
        long id,
        [FromBody] RequestAuditArchiveRestoreInput input)
    {
        return _queryAppService.RequestArchiveRestoreAsync(id, input);
    }

    [HttpGet("archive/restores")]
    [Authorize(Policy = AuditLogsPermissions.Archive.Read)]
    public Task<PagedResultDto<AuditArchiveRestoreJobDto>> GetArchiveRestoreJobsAsync(
        [FromQuery] GetAuditArchiveRestoreJobsInput input)
    {
        return _queryAppService.GetArchiveRestoreJobsAsync(input);
    }

    [HttpGet("archive/restores/{id:long}")]
    [Authorize(Policy = AuditLogsPermissions.Archive.Read)]
    public Task<AuditArchiveRestoreJobDto> GetArchiveRestoreJobAsync(long id)
    {
        return _queryAppService.GetArchiveRestoreJobAsync(id);
    }

    [HttpPost("archive/restores/{id:long}/cancel")]
    [Authorize(Policy = AuditLogsPermissions.Archive.Restore)]
    [Audited]
    public Task<AuditArchiveRestoreJobDto> CancelArchiveRestoreJobAsync(long id)
    {
        return _queryAppService.CancelArchiveRestoreJobAsync(id);
    }

    [HttpPost("archive/restores/{id:long}/retry")]
    [Authorize(Policy = AuditLogsPermissions.Archive.Restore)]
    [Audited]
    public Task<AuditArchiveRestoreJobDto> RetryArchiveRestoreJobAsync(long id)
    {
        return _queryAppService.RetryArchiveRestoreJobAsync(id);
    }

    [HttpPost("seals/verify")]
    [Authorize(Policy = AuditLogsPermissions.Archive.VerifyIntegrity)]
    public Task<AuditLogSealVerificationDto> VerifySealRangeAsync([FromBody] VerifyAuditLogSealRangeInput input)
    {
        return _queryAppService.VerifySealRangeAsync(input);
    }

    [HttpGet("seals/verifications")]
    [Authorize(Policy = AuditLogsPermissions.Archive.VerifyIntegrity)]
    public Task<PagedResultDto<AuditLogSealVerificationDto>> GetSealVerificationsAsync(
        [FromQuery] GetAuditLogSealVerificationsInput input)
    {
        return _queryAppService.GetSealVerificationsAsync(input);
    }

    [HttpGet("seals/verifications/{id:long}")]
    [Authorize(Policy = AuditLogsPermissions.Archive.VerifyIntegrity)]
    public Task<AuditLogSealVerificationDto> GetSealVerificationAsync(long id)
    {
        return _queryAppService.GetSealVerificationAsync(id);
    }

    [HttpGet("summary")]
    [Authorize(Policy = AuditLogsPermissions.Read)]
    public Task<AuditLogSummaryReportDto> GetSummaryReportAsync([FromQuery] GetAuditLogSummaryInput input)
    {
        return _queryAppService.GetSummaryReportAsync(input);
    }

    [HttpGet("legal-holds")]
    [Authorize(Policy = AuditLogsPermissions.LegalHold.Read)]
    public Task<PagedResultDto<AuditLogLegalHoldDto>> GetLegalHoldsAsync([FromQuery] GetAuditLogLegalHoldsInput input)
    {
        return _legalHoldAppService.GetListAsync(input);
    }

    [HttpPost("legal-holds")]
    [Authorize(Policy = AuditLogsPermissions.LegalHold.Create)]
    [Audited]
    public Task<AuditLogLegalHoldDto> CreateLegalHoldAsync([FromBody] CreateAuditLogLegalHoldInput input)
    {
        return _legalHoldAppService.CreateAsync(input);
    }

    [HttpPost("legal-holds/{id:long}/release")]
    [Authorize(Policy = AuditLogsPermissions.LegalHold.Release)]
    [Audited]
    public Task<AuditLogLegalHoldDto> ReleaseLegalHoldAsync(long id, [FromBody] ReleaseAuditLogLegalHoldInput input)
    {
        return _legalHoldAppService.ReleaseAsync(id, input);
    }
}
