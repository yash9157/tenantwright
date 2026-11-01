using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Permissions.Application.Contracts.Authorization;
using Permissions.Application.Contracts.Common;
using Permissions.Application.Contracts.PermissionApproval;
using Shared.AuditLogs.Auditing;
using Permissions.Application.PermissionManagement;

namespace Permissions.Controllers.PermissionApproval;

[ApiController]
[Route("api/permissions/grant-change-requests")]
public class PermissionApprovalController : ControllerBase
{
    private readonly IPermissionApprovalAppService _service;

    public PermissionApprovalController(IPermissionApprovalAppService service)
    {
        _service = service;
    }

    [HttpGet]
    [Authorize(Policy = PermissionsPolicies.ReadGrantApprovals)]
    public Task<PagedResultDto<PermissionGrantChangeRequestDto>> GetListAsync(
        [FromQuery] PermissionGrantChangeRequestListInput input,
        CancellationToken cancellationToken)
    {
        return _service.GetListAsync(input, cancellationToken);
    }

    [HttpGet("mine")]
    [Authorize(Policy = PermissionsPolicies.ManagePermissionGrants)]
    public Task<PagedResultDto<PermissionGrantChangeRequestDto>> GetMineAsync(
        [FromQuery] PermissionGrantChangeRequestListInput input,
        CancellationToken cancellationToken)
    {
        return _service.GetMineAsync(input, cancellationToken);
    }

    [HttpGet("pending-count")]
    [Authorize(Policy = PermissionsPolicies.ReadGrantApprovals)]
    public Task<PermissionApprovalPendingCountDto> GetPendingCountAsync(CancellationToken cancellationToken)
    {
        return _service.GetPendingCountAsync(cancellationToken);
    }

    [HttpPost("{id:guid}/approve")]
    [Authorize(Policy = PermissionsPolicies.DecideGrantApprovals)]
    [Audited(Category = "permissions.grant")]
    public Task<ActionResult<PermissionGrantChangeRequestDto>> ApproveAsync(
        Guid id,
        [FromBody] DecideGrantChangeRequestInput input,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(() => _service.ApproveAsync(id, input, cancellationToken));
    }

    [HttpPost("{id:guid}/reject")]
    [Authorize(Policy = PermissionsPolicies.DecideGrantApprovals)]
    [Audited(Category = "permissions.grant")]
    public Task<ActionResult<PermissionGrantChangeRequestDto>> RejectAsync(
        Guid id,
        [FromBody] DecideGrantChangeRequestInput input,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(() => _service.RejectAsync(id, input, cancellationToken));
    }

    [HttpPost("{id:guid}/cancel")]
    [Authorize(Policy = PermissionsPolicies.ManagePermissionGrants)]
    [Audited(Category = "permissions.grant")]
    public Task<ActionResult<PermissionGrantChangeRequestDto>> CancelAsync(
        Guid id,
        [FromBody] CancelGrantChangeRequestInput input,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(() => _service.CancelAsync(id, input, cancellationToken));
    }

    [HttpPost("{id:guid}/cancel-any")]
    [Authorize(Policy = PermissionsPolicies.CancelAnyGrantApproval)]
    [Audited(Category = "permissions.grant")]
    public Task<ActionResult<PermissionGrantChangeRequestDto>> CancelAnyAsync(
        Guid id,
        [FromBody] CancelGrantChangeRequestInput input,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(() => _service.CancelAnyAsync(id, input, cancellationToken));
    }

    [HttpPost("bulk-decisions")]
    [Authorize(Policy = PermissionsPolicies.DecideGrantApprovals)]
    [Audited(Category = "permissions.grant")]
    public async Task<ActionResult<PermissionApprovalBulkDecisionResultDto>> DecideBulkAsync(
        [FromBody] PermissionApprovalBulkDecisionInput input,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _service.DecideBulkAsync(input, cancellationToken);
        }
        catch (PermissionApprovalConflictException exception)
        {
            return Conflict(exception.Conflict);
        }
    }

    private static async Task<ActionResult<PermissionGrantChangeRequestDto>> ExecuteAsync(
        Func<Task<PermissionGrantChangeRequestDto>> action)
    {
        try
        {
            return await action();
        }
        catch (PermissionApprovalConflictException exception)
        {
            return new ConflictObjectResult(exception.Conflict);
        }
    }
}
