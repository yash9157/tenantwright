using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Permissions.Application.Contracts.Authorization;
using Permissions.Application.Contracts.ImportExport;
using Permissions.Application.ImportExport;
using Shared.AuditLogs.Auditing;

namespace Permissions.Controllers.ImportExport;

[ApiController]
[Route("api/permissions/import-jobs")]
public class PermissionImportJobsController : ControllerBase
{
    private readonly IPermissionImportJobAppService _service;

    public PermissionImportJobsController(IPermissionImportJobAppService service)
    {
        _service = service;
    }

    [HttpGet]
    [Authorize(Policy = PermissionsPolicies.ReadPermissionTransfers)]
    public Task<PagedPermissionImportJobsDto> GetListAsync([FromQuery] PermissionImportJobListInput input, CancellationToken cancellationToken)
        => _service.GetListAsync(input, cancellationToken);

    [HttpGet("{jobId:guid}")]
    [Authorize(Policy = PermissionsPolicies.ReadPermissionTransfers)]
    public async Task<ActionResult<PermissionImportJobDto>> GetAsync(Guid jobId, CancellationToken cancellationToken)
    {
        try { return await _service.GetAsync(jobId, cancellationToken); }
        catch (PermissionImportJobConflictException ex) { return Conflict(ex.Conflict); }
    }

    [HttpPost]
    [Authorize(Policy = PermissionsPolicies.ValidatePermissionImports)]
    [Audited]
    public Task<PermissionImportJobCreatedDto> CreateAsync([FromBody] CreatePermissionImportJobDto input, CancellationToken cancellationToken)
        => _service.CreateAsync(input, cancellationToken);

    [HttpPost("{jobId:guid}/apply")]
    [Authorize(Policy = PermissionsPolicies.ApplyPermissionImports)]
    [Audited]
    public async Task<ActionResult<PermissionImportJobDto>> ApplyAsync(Guid jobId, [FromBody] ApplyPermissionImportJobDto input, CancellationToken cancellationToken)
    {
        try { return await _service.ApplyAsync(jobId, input, cancellationToken); }
        catch (PermissionImportJobConflictException ex) { return Conflict(ex.Conflict); }
    }

    [HttpPost("{jobId:guid}/cancel")]
    [Authorize(Policy = PermissionsPolicies.ValidatePermissionImports)]
    [Audited]
    public async Task<ActionResult<PermissionImportJobDto>> CancelAsync(Guid jobId, CancellationToken cancellationToken)
    {
        try { return await _service.CancelAsync(jobId, cancellationToken); }
        catch (PermissionImportJobConflictException ex) { return Conflict(ex.Conflict); }
    }

    [HttpPost("{jobId:guid}/retry")]
    [Authorize(Policy = PermissionsPolicies.ValidatePermissionImports)]
    [Audited]
    public async Task<ActionResult<PermissionImportJobCreatedDto>> RetryAsync(Guid jobId, CancellationToken cancellationToken)
    {
        try { return await _service.RetryAsync(jobId, cancellationToken); }
        catch (PermissionImportJobConflictException ex) { return Conflict(ex.Conflict); }
    }
}
