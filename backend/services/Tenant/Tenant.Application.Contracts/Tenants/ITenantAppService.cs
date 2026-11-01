namespace Tenant.Application.Contracts.Tenants;

using Tenant.Domain.Shared.Enums;

public interface ITenantAppService
{
    Task<TenantDto> GetAsync(Guid id);
    Task<TenantDetailDto> GetDetailAsync(Guid id, bool includeDeleted = false);
    Task<PagedResultDto<TenantListItemDto>> GetListAsync(GetTenantsInput input);
    Task<IReadOnlyList<TenantSelectionOptionDto>> GetSelectionOptionsAsync(
        GetTenantSelectionOptionsInput input,
        CancellationToken cancellationToken = default);
    Task<CurrentTenantProfileDto> GetCurrentProfileAsync();
    Task<TenantOrganizationProfileDto> GetCurrentOrganizationProfileAsync();
    Task<TenantOrganizationProfileDto> GetOrganizationProfileAsync(Guid id);
    Task<TenantOrganizationProfileDto> UpdateOrganizationProfileAsync(Guid id, TenantOrganizationProfileUpdateInput input);
    Task<TenantOrganizationProfileDto> UpdateCurrentOrganizationProfileAsync(TenantOrganizationProfileUpdateInput input);
    Task<TenantLegalComplianceProfileDto> GetLegalComplianceProfileAsync(Guid id);
    Task<TenantLegalComplianceProfileDto> UpdateLegalComplianceProfileAsync(Guid id, TenantLegalComplianceProfileUpdateInput input);
    Task<TenantDataGovernanceProfileDto> GetDataGovernanceProfileAsync(Guid id);
    Task<TenantDataGovernanceProfileDto> UpdateDataGovernanceProfileAsync(Guid id, TenantDataGovernanceProfileUpdateInput input);
    Task<TenantDto> CreateAsync(TenantCreateDto input);
    Task<TenantOwnershipRequestDto> CreateWithInitialOwnerAsync(TenantCreateWithInitialOwnerInput input);
    Task<PublicTenantRegistrationInitiateResult> InitiatePublicRegistrationAsync(PublicTenantRegistrationInitiateInput input);
    Task<TenantProvisioningOperationDto> GetProvisioningOperationAsync(Guid tenantId);
    Task<TenantProvisioningOperationDto?> GetPublicRegistrationAsync(Guid operationId);
    Task<TenantProvisioningOperationDto> RetryProvisioningOperationAsync(Guid tenantId, TenantProvisioningRetryInput input);
    Task<TenantProvisioningOperationDto> RetryPublicRegistrationAsync(Guid operationId);
    Task<int> CleanupExpiredPublicRegistrationsAsync(DateTime utcNow, int expiryHours, int batchSize, CancellationToken cancellationToken = default);
    Task<TenantOwnershipRequestDto> RequestOwnershipTransferAsync(Guid id, TenantOwnershipTransferInput input);
    Task<PagedResultDto<TenantOwnershipRequestDto>> GetOwnershipHistoryAsync(Guid id, GetTenantOwnershipHistoryInput input);
    Task CancelOwnershipRequestAsync(Guid id, Guid requestId);
    Task<TenantOwnershipRequestDto> ResendOwnershipRequestAsync(Guid id, Guid requestId);
    Task<TenantClosureRequestDto> RequestClosureAsync(Guid id, TenantClosureRequestInput input);
    Task<TenantClosureStatusDto?> GetCurrentClosureRequestAsync();
    Task<TenantClosureRequestDto> ApproveClosureAsync(Guid id, Guid requestId, TenantClosureActionInput input);
    Task<TenantClosureRequestDto> RejectClosureAsync(Guid id, Guid requestId, TenantClosureActionInput input);
    Task<TenantClosureRequestDto> RestoreClosureAsync(Guid id, Guid requestId, TenantClosureActionInput input);
    Task CancelClosureAsync(Guid id, Guid requestId);
    Task<PagedResultDto<TenantClosureRequestDto>> GetClosureHistoryAsync(Guid id, GetTenantClosureHistoryInput input);
    Task<TenantDto> UpdateAsync(Guid id, TenantUpdateDto input);
    Task<TenantDto> RenameAsync(Guid id, TenantRenameInput input);
    Task DeleteAsync(Guid id);
    Task<TenantArchiveResultDto> ArchiveAsync(Guid id, TenantArchiveInput input);
    Task<TenantArchiveResultDto> ArchiveOverrideAsync(Guid id, TenantArchiveInput input);
    Task RestoreAsync(Guid id, TenantRestoreInput input);
    Task<TenantRestoreResultDto> RestoreDetailedAsync(Guid id, TenantRestoreInput input);
    Task<TenantDto> TransitionLifecycleAsync(Guid id, TenantLifecycleStatus targetStatus, TenantLifecycleTransitionInput input);
    Task<PagedResultDto<TenantLifecycleTransitionDto>> GetLifecycleHistoryAsync(Guid id, GetTenantLifecycleHistoryInput input);
    Task<PagedResultDto<TenantHistoryEntryDto>> GetHistoryAsync(Guid id, GetTenantHistoryInput input);
    Task<TenantHistoryCursorPageDto> GetHistoryCursorAsync(Guid id, GetTenantHistoryCursorInput input);
    Task<TenantOperationalSummaryDto> GetOperationalSummaryAsync(Guid id);
    Task<TenantActionImpactDto> GetActionImpactAsync(Guid id, TenantActionImpactAction action);
    Task DeleteProvisioningAsync(Guid id);

}
