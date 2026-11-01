using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Permissions.Application.Contracts.Authorization;
using Permissions.Application.Contracts.PermissionConflictRules;
using Permissions.Application.Grants;
using Shared.AuditLogs.Auditing;

namespace Permissions.Controllers.PermissionConflictRules;

[ApiController]
[Route("api/permissions/permission-conflict-rules")]
public class PermissionConflictRulesController : ControllerBase
{
    private readonly IPermissionConflictRuleAppService _service;

    public PermissionConflictRulesController(IPermissionConflictRuleAppService service)
    {
        _service = service;
    }

    [HttpGet]
    [Authorize(Policy = PermissionsPolicies.ReadPermissionConflictRules)]
    public Task<List<PermissionConflictRuleDto>> GetRulesAsync(CancellationToken cancellationToken)
        => _service.GetRulesAsync(cancellationToken);

    [HttpGet("impact")]
    [Authorize(Policy = PermissionsPolicies.ReadPermissionConflictRules)]
    public Task<PermissionConflictRuleImpactDto> GetImpactAsync(
        [FromQuery] string permissionA,
        [FromQuery] string permissionB,
        CancellationToken cancellationToken)
        => _service.GetImpactAsync(permissionA, permissionB, cancellationToken);

    [HttpGet("change-requests")]
    [Authorize(Policy = PermissionsPolicies.ReadPermissionConflictRules)]
    public Task<PagedPermissionConflictRuleChangeRequestsDto> GetChangeRequestsAsync(
        [FromQuery] ConflictRuleChangeRequestListInput input,
        CancellationToken cancellationToken)
        => _service.GetChangeRequestsAsync(input, cancellationToken);

    [HttpPost("change-requests")]
    [Authorize(Policy = PermissionsPolicies.CreatePermissionConflictRules)]
    [Audited]
    public Task<ActionResult<PermissionConflictRuleChangeRequestDto>> RequestCreateAsync(
        [FromBody] RequestCreatePermissionConflictRuleDto input,
        CancellationToken cancellationToken)
        => RunAsync(() => _service.RequestCreateAsync(input, cancellationToken));

    [HttpPost("{ruleId:guid}/update-requests")]
    [Authorize(Policy = PermissionsPolicies.UpdatePermissionConflictRules)]
    [Audited]
    public Task<ActionResult<PermissionConflictRuleChangeRequestDto>> RequestUpdateAsync(
        Guid ruleId,
        [FromBody] RequestUpdatePermissionConflictRuleDto input,
        CancellationToken cancellationToken)
        => RunAsync(() => _service.RequestUpdateAsync(ruleId, input, cancellationToken));

    [HttpPost("{ruleId:guid}/delete-requests")]
    [Authorize(Policy = PermissionsPolicies.DeletePermissionConflictRules)]
    [Audited]
    public Task<ActionResult<PermissionConflictRuleChangeRequestDto>> RequestDeleteAsync(
        Guid ruleId,
        [FromBody] RequestDeletePermissionConflictRuleDto input,
        CancellationToken cancellationToken)
        => RunAsync(() => _service.RequestDeleteAsync(ruleId, input, cancellationToken));

    [HttpPost("change-requests/{changeRequestId:guid}/approve")]
    [Authorize(Policy = PermissionsPolicies.DecidePermissionConflictRules)]
    [Audited]
    public Task<ActionResult<PermissionConflictRuleChangeRequestDto>> ApproveAsync(
        Guid changeRequestId,
        [FromBody] DecidePermissionConflictRuleChangeDto input,
        CancellationToken cancellationToken)
        => RunAsync(() => _service.ApproveAsync(changeRequestId, input, cancellationToken));

    [HttpPost("change-requests/{changeRequestId:guid}/reject")]
    [Authorize(Policy = PermissionsPolicies.DecidePermissionConflictRules)]
    [Audited]
    public Task<ActionResult<PermissionConflictRuleChangeRequestDto>> RejectAsync(
        Guid changeRequestId,
        [FromBody] DecidePermissionConflictRuleChangeDto input,
        CancellationToken cancellationToken)
        => RunAsync(() => _service.RejectAsync(changeRequestId, input, cancellationToken));

    [HttpPost("change-requests/{changeRequestId:guid}/cancel")]
    [Authorize(Policy = PermissionsPolicies.ReadPermissionConflictRules)]
    [Audited]
    public async Task<IActionResult> CancelAsync(Guid changeRequestId, CancellationToken cancellationToken)
    {
        try
        {
            await _service.CancelAsync(changeRequestId, cancellationToken);
            return NoContent();
        }
        catch (PermissionConflictRuleChangeConflictException exception)
        {
            return Conflict(exception.Conflict);
        }
    }

    private async Task<ActionResult<PermissionConflictRuleChangeRequestDto>> RunAsync(
        Func<Task<PermissionConflictRuleChangeRequestDto>> action)
    {
        try
        {
            return await action();
        }
        catch (PermissionConflictRuleChangeConflictException exception)
        {
            return Conflict(exception.Conflict);
        }
    }
}
