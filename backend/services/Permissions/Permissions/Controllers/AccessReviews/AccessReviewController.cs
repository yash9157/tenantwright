using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Permissions.Application.Contracts.AccessReviews;
using Permissions.Application.Contracts.Authorization;
using Shared.AuditLogs.Auditing;

namespace Permissions.Controllers.AccessReviews;

[ApiController]
[Route("api/permissions/access-reviews")]
[Authorize(Policy = PermissionsPolicies.ManageAccessReviews)]
public sealed class AccessReviewController : ControllerBase
{
    private readonly IAccessReviewAppService _service;

    public AccessReviewController(IAccessReviewAppService service)
    {
        _service = service;
    }

    [HttpPost]
    [Audited(Category = "permissions.review")]
    public Task<AccessReviewCampaignDto> CreateCampaignAsync(
        [FromBody] CreateAccessReviewCampaignInput input,
        CancellationToken cancellationToken)
        => _service.CreateCampaignAsync(input, cancellationToken);

    [HttpGet]
    public Task<PagedAccessReviewCampaignResultDto> GetCampaignsAsync(
        [FromQuery] GetAccessReviewCampaignsInput input,
        CancellationToken cancellationToken)
        => _service.GetCampaignsAsync(input, cancellationToken);

    [HttpGet("{campaignId:guid}/items")]
    public Task<PagedAccessReviewItemResultDto> GetItemsAsync(
        Guid campaignId,
        [FromQuery] GetAccessReviewItemsInput input,
        CancellationToken cancellationToken)
        => _service.GetItemsAsync(campaignId, input, cancellationToken);

    [HttpPost("{campaignId:guid}/items/{itemId:guid}/decision")]
    [Audited(Category = "permissions.review")]
    public async Task<ActionResult<AccessReviewItemDto>> DecideItemAsync(
        Guid campaignId,
        Guid itemId,
        [FromBody] DecideAccessReviewItemInput input,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _service.DecideItemAsync(campaignId, itemId, input, cancellationToken);
        }
        catch (AccessReviewConflictException exception)
        {
            return Conflict(exception.Conflict);
        }
    }

    [HttpPost("{campaignId:guid}/close")]
    [Audited(Category = "permissions.review")]
    public Task<AccessReviewCampaignDto> CloseCampaignAsync(
        Guid campaignId,
        [FromBody] CloseAccessReviewCampaignInput input,
        CancellationToken cancellationToken)
        => _service.CloseCampaignAsync(campaignId, input, cancellationToken);

    [HttpGet("role-privilege-matrix")]
    public Task<RolePrivilegeMatrixDto> GetRolePrivilegeMatrixAsync(
        [FromQuery] GetRolePrivilegeMatrixInput input,
        CancellationToken cancellationToken)
        => _service.GetRolePrivilegeMatrixAsync(input, cancellationToken);
}
