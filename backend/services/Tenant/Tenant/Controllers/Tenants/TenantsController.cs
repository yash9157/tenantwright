using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Shared.Permissions.Definitions.Tenant;
using Tenant.Application.Contracts.Tenants;
using Shared.AuditLogs.Auditing;
using Tenant.Domain.Shared.Enums;

namespace Tenant.Controllers.Tenants;

[ApiController]
[Route("api/tenant/tenants")]
[Authorize(Policy = TenantPermissions.Management.Default)]
public class TenantsController : ControllerBase
{
    private readonly ITenantAppService _tenantAppService;

    public TenantsController(ITenantAppService tenantAppService)
    {
        _tenantAppService = tenantAppService;
    }

    [HttpGet("{id:guid}/organization-profile")]
    public Task<TenantOrganizationProfileDto> GetOrganizationProfileAsync(Guid id) => _tenantAppService.GetOrganizationProfileAsync(id);
    [HttpPut("{id:guid}/organization-profile")]
    [Authorize(Policy = TenantPermissions.Profile.Update)]
    [Audited(Category = "tenant.profile")]
    public Task<TenantOrganizationProfileDto> UpdateOrganizationProfileAsync(Guid id, [FromBody] TenantOrganizationProfileUpdateInput input) => _tenantAppService.UpdateOrganizationProfileAsync(id, input);

    [HttpGet("{id:guid}/legal-compliance-profile")]
    [Authorize(Policy = TenantPermissions.Compliance.Read)]
    public Task<TenantLegalComplianceProfileDto> GetLegalComplianceProfileAsync(Guid id)
        => _tenantAppService.GetLegalComplianceProfileAsync(id);

    [HttpPut("{id:guid}/legal-compliance-profile")]
    [Authorize(Policy = TenantPermissions.Compliance.Update)]
    [Audited(Category = "tenant.compliance")]
    public Task<TenantLegalComplianceProfileDto> UpdateLegalComplianceProfileAsync(Guid id, [FromBody] TenantLegalComplianceProfileUpdateInput input)
        => _tenantAppService.UpdateLegalComplianceProfileAsync(id, input);

    [HttpGet("{id:guid}/data-governance-profile")]
    [Authorize(Policy = TenantPermissions.DataGovernance.Read)]
    public Task<TenantDataGovernanceProfileDto> GetDataGovernanceProfileAsync(Guid id)
        => _tenantAppService.GetDataGovernanceProfileAsync(id);

    [HttpPut("{id:guid}/data-governance-profile")]
    [Authorize(Policy = TenantPermissions.DataGovernance.Update)]
    [Audited(Category = "tenant.data-governance")]
    public Task<TenantDataGovernanceProfileDto> UpdateDataGovernanceProfileAsync(Guid id, [FromBody] TenantDataGovernanceProfileUpdateInput input)
        => _tenantAppService.UpdateDataGovernanceProfileAsync(id, input);

    [HttpGet("{id:guid}")]
    public Task<TenantDto> GetAsync(Guid id)
    {
        return _tenantAppService.GetAsync(id);
    }

    [HttpGet("{id:guid}/detail")]
    public Task<TenantDetailDto> GetDetailAsync(Guid id, [FromQuery] bool includeDeleted = false)
    {
        return _tenantAppService.GetDetailAsync(id, includeDeleted);
    }

    [HttpGet("{id:guid}/history")]
    public Task<PagedResultDto<TenantHistoryEntryDto>> GetHistoryAsync(Guid id, [FromQuery] GetTenantHistoryInput input)
    {
        return _tenantAppService.GetHistoryAsync(id, input);
    }

    [HttpGet("{id:guid}/history/cursor")]
    public Task<TenantHistoryCursorPageDto> GetHistoryCursorAsync(Guid id, [FromQuery] GetTenantHistoryCursorInput input)
    {
        return _tenantAppService.GetHistoryCursorAsync(id, input);
    }

    [HttpGet("{id:guid}/operational-summary")]
    public Task<TenantOperationalSummaryDto> GetOperationalSummaryAsync(Guid id)
    {
        return _tenantAppService.GetOperationalSummaryAsync(id);
    }

    [HttpGet("{id:guid}/impact")]
    public Task<TenantActionImpactDto> GetActionImpactAsync(Guid id, [FromQuery] TenantActionImpactAction action)
    {
        return _tenantAppService.GetActionImpactAsync(id, action);
    }

    [HttpGet("{id:guid}/provisioning-operation")]
    public Task<TenantProvisioningOperationDto> GetProvisioningOperationAsync(Guid id)
        => _tenantAppService.GetProvisioningOperationAsync(id);

    [HttpPost("{id:guid}/provisioning-operation/retry")]
    [Authorize(Policy = TenantPermissions.Provisioning.Retry)]
    [Audited(Category = "tenant.provisioning")]
    public Task<TenantProvisioningOperationDto> RetryProvisioningOperationAsync(
        Guid id,
        [FromBody] TenantProvisioningRetryInput input)
        => _tenantAppService.RetryProvisioningOperationAsync(id, input);

    [HttpGet]
    public Task<PagedResultDto<TenantListItemDto>> GetListAsync([FromQuery] GetTenantsInput input)
    {
        return _tenantAppService.GetListAsync(input);
    }

    [HttpGet("selection-options")]
    public Task<IReadOnlyList<TenantSelectionOptionDto>> GetSelectionOptionsAsync(
        [FromQuery] GetTenantSelectionOptionsInput input,
        CancellationToken cancellationToken)
    {
        return _tenantAppService.GetSelectionOptionsAsync(input, cancellationToken);
    }

    [HttpPost]
    [Authorize(Policy = TenantPermissions.Management.Create)]
    [Audited(Category = "tenant.lifecycle")]
    public Task<TenantDto> CreateAsync([FromBody] TenantCreateDto input)
    {
        return _tenantAppService.CreateAsync(input);
    }

    [HttpPost("with-initial-owner")]
    [Authorize(Policy = TenantPermissions.Management.Create)]
    [Audited(Category = "tenant.ownership")]
    public Task<TenantOwnershipRequestDto> CreateWithInitialOwnerAsync([FromBody] TenantCreateWithInitialOwnerInput input)
    {
        return _tenantAppService.CreateWithInitialOwnerAsync(input);
    }

    [HttpPost("{id:guid}/ownership-transfer")]
    [Authorize(Policy = TenantPermissions.Ownership.Transfer)]
    [Audited(Category = "tenant.ownership")]
    public Task<TenantOwnershipRequestDto> RequestOwnershipTransferAsync(Guid id, [FromBody] TenantOwnershipTransferInput input)
        => _tenantAppService.RequestOwnershipTransferAsync(id, input);

    [HttpGet("{id:guid}/ownership-history")]
    [Authorize(Policy = TenantPermissions.Ownership.Transfer)]
    public Task<PagedResultDto<TenantOwnershipRequestDto>> GetOwnershipHistoryAsync(Guid id, [FromQuery] GetTenantOwnershipHistoryInput input)
        => _tenantAppService.GetOwnershipHistoryAsync(id, input);

    [HttpDelete("{id:guid}/ownership-requests/{requestId:guid}")]
    [Authorize(Policy = TenantPermissions.Ownership.Transfer)]
    [Audited(Category = "tenant.ownership")]
    public Task CancelOwnershipRequestAsync(Guid id, Guid requestId)
        => _tenantAppService.CancelOwnershipRequestAsync(id, requestId);

    [HttpPost("{id:guid}/ownership-requests/{requestId:guid}/resend")]
    [Authorize(Policy = TenantPermissions.Ownership.Transfer)]
    [Audited(Category = "tenant.ownership")]
    public Task<TenantOwnershipRequestDto> ResendOwnershipRequestAsync(Guid id, Guid requestId)
        => _tenantAppService.ResendOwnershipRequestAsync(id, requestId);

    [HttpPost("{id:guid}/closure-requests")]
    [Authorize(Policy = TenantPermissions.Closure.Request)]
    [Audited(Category = "tenant.closure")]
    public Task<TenantClosureRequestDto> RequestClosureAsync(Guid id, [FromBody] TenantClosureRequestInput input) => _tenantAppService.RequestClosureAsync(id, input);

    [HttpPost("{id:guid}/closure-requests/{requestId:guid}/approve")]
    [Authorize(Policy = TenantPermissions.Closure.Manage)]
    [Audited(Category = "tenant.closure")]
    public Task<TenantClosureRequestDto> ApproveClosureAsync(Guid id, Guid requestId, [FromBody] TenantClosureActionInput input) => _tenantAppService.ApproveClosureAsync(id, requestId, input);

    [HttpPost("{id:guid}/closure-requests/{requestId:guid}/reject")]
    [Authorize(Policy = TenantPermissions.Closure.Manage)]
    [Audited]
    public Task<TenantClosureRequestDto> RejectClosureAsync(Guid id, Guid requestId, [FromBody] TenantClosureActionInput input) => _tenantAppService.RejectClosureAsync(id, requestId, input);

    [HttpPost("{id:guid}/closure-requests/{requestId:guid}/restore")]
    [Authorize(Policy = TenantPermissions.Closure.Manage)]
    [Audited(Category = "tenant.closure")]
    public Task<TenantClosureRequestDto> RestoreClosureAsync(Guid id, Guid requestId, [FromBody] TenantClosureActionInput input) => _tenantAppService.RestoreClosureAsync(id, requestId, input);

    [HttpDelete("{id:guid}/closure-requests/{requestId:guid}")]
    [Authorize(Policy = TenantPermissions.Closure.Request)]
    [Audited(Category = "tenant.closure")]
    public Task CancelClosureAsync(Guid id, Guid requestId) => _tenantAppService.CancelClosureAsync(id, requestId);

    [HttpGet("{id:guid}/closure-history")]
    [Authorize(Policy = TenantPermissions.Closure.Manage)]
    public Task<PagedResultDto<TenantClosureRequestDto>> GetClosureHistoryAsync(Guid id, [FromQuery] GetTenantClosureHistoryInput input) => _tenantAppService.GetClosureHistoryAsync(id, input);

    [HttpPut("{id:guid}")]
    [Authorize(Policy = TenantPermissions.Management.Update)]
    [Audited(Category = "tenant.lifecycle")]
    [Obsolete("Use the name-only endpoint or lifecycle transition endpoint instead.")]
    public Task<TenantDto> UpdateAsync(Guid id, [FromBody] TenantUpdateDto input)
    {
        return _tenantAppService.UpdateAsync(id, input);
    }

    [HttpPut("{id:guid}/name")]
    [Authorize(Policy = TenantPermissions.Management.Update)]
    [Audited(Category = "tenant.identity")]
    public Task<TenantDto> RenameAsync(Guid id, [FromBody] TenantRenameInput input)
        => _tenantAppService.RenameAsync(id, input);

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = TenantPermissions.Management.Delete)]
    [Audited(Category = "tenant.lifecycle")]
    public Task DeleteAsync(Guid id)
    {
        return _tenantAppService.DeleteAsync(id);
    }

    [HttpPost("{id:guid}/archive")]
    [Authorize(Policy = TenantPermissions.Management.Archive)]
    [Audited(Category = "tenant.lifecycle")]
    public Task<TenantArchiveResultDto> ArchiveAsync(Guid id, [FromBody] TenantArchiveInput input)
        => _tenantAppService.ArchiveAsync(id, input);

    [HttpPost("{id:guid}/archive-override")]
    [Authorize(Policy = TenantPermissions.BreakGlass.ArchiveOverride)]
    [Audited(Category = "tenant.lifecycle")]
    public Task<TenantArchiveResultDto> ArchiveOverrideAsync(Guid id, [FromBody] TenantArchiveInput input)
        => _tenantAppService.ArchiveOverrideAsync(id, input);

    [HttpPost("{id:guid}/restore")]
    [Authorize(Policy = TenantPermissions.Management.Restore)]
    [Audited(Category = "tenant.lifecycle")]
    [Obsolete("Use the typed restore-detailed endpoint instead.")]
    public Task RestoreAsync(Guid id, [FromBody] TenantRestoreInput? input)
    {
        return _tenantAppService.RestoreAsync(id, input ?? new TenantRestoreInput());
    }

    [HttpPost("{id:guid}/restore-detailed")]
    [Authorize(Policy = TenantPermissions.Management.Restore)]
    [Audited(Category = "tenant.lifecycle")]
    public Task<TenantRestoreResultDto> RestoreDetailedAsync(Guid id, [FromBody] TenantRestoreInput input)
        => _tenantAppService.RestoreDetailedAsync(id, input);

    [HttpPost("{id:guid}/lifecycle/{targetStatus}")]
    [Authorize(Policy = TenantPermissions.Lifecycle.Manage)]
    [Audited(Category = "tenant.lifecycle")]
    public Task<TenantDto> TransitionLifecycleAsync(
        Guid id,
        TenantLifecycleStatus targetStatus,
        [FromBody] TenantLifecycleTransitionInput input)
    {
        return _tenantAppService.TransitionLifecycleAsync(id, targetStatus, input);
    }

    [HttpGet("{id:guid}/lifecycle-history")]
    [Authorize(Policy = TenantPermissions.Lifecycle.Manage)]
    public Task<PagedResultDto<TenantLifecycleTransitionDto>> GetLifecycleHistoryAsync(
        Guid id,
        [FromQuery] GetTenantLifecycleHistoryInput input)
    {
        return _tenantAppService.GetLifecycleHistoryAsync(id, input);
    }
}
