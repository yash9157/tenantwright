namespace Permissions.Application.Contracts.PermissionConflictRules;

/// <summary>
/// Separation-of-duty conflict-rule management. All mutations flow through a four-eyes change
/// request: a requester proposes a create/update/delete and a different operator holding
/// <c>Permissions.Management.ConflictRules.Decide</c> approves (applies) or rejects it.
/// </summary>
public interface IPermissionConflictRuleAppService
{
    Task<List<PermissionConflictRuleDto>> GetRulesAsync(CancellationToken cancellationToken = default);

    /// <summary>Blast-radius preview for a proposed pair before requesting a rule.</summary>
    Task<PermissionConflictRuleImpactDto> GetImpactAsync(string permissionA, string permissionB, CancellationToken cancellationToken = default);

    Task<PagedPermissionConflictRuleChangeRequestsDto> GetChangeRequestsAsync(ConflictRuleChangeRequestListInput input, CancellationToken cancellationToken = default);

    Task<PermissionConflictRuleChangeRequestDto> RequestCreateAsync(RequestCreatePermissionConflictRuleDto input, CancellationToken cancellationToken = default);

    Task<PermissionConflictRuleChangeRequestDto> RequestUpdateAsync(Guid ruleId, RequestUpdatePermissionConflictRuleDto input, CancellationToken cancellationToken = default);

    Task<PermissionConflictRuleChangeRequestDto> RequestDeleteAsync(Guid ruleId, RequestDeletePermissionConflictRuleDto input, CancellationToken cancellationToken = default);

    /// <summary>Approves and transactionally applies a pending change request. Requester cannot approve their own.</summary>
    Task<PermissionConflictRuleChangeRequestDto> ApproveAsync(Guid changeRequestId, DecidePermissionConflictRuleChangeDto input, CancellationToken cancellationToken = default);

    Task<PermissionConflictRuleChangeRequestDto> RejectAsync(Guid changeRequestId, DecidePermissionConflictRuleChangeDto input, CancellationToken cancellationToken = default);

    /// <summary>The original requester withdraws their own pending request.</summary>
    Task CancelAsync(Guid changeRequestId, CancellationToken cancellationToken = default);
}
