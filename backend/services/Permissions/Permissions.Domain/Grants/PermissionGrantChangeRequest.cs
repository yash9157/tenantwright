using Shared;
using Shared.Domain;
using Shared.Domain.Entities;
using Permissions.Domain.Shared.Constants;
using Permissions.Domain.Shared.Enums;
using Shared.AuditLogs.Auditing;

namespace Permissions.Domain.Grants;

/// <summary>
/// A pending (or decided) request to grant a permission whose definition is marked
/// <c>RequiresApproval</c> (flow 6). Granting such a permission does not write a <see cref="PermissionGrant"/>
/// directly; it records one of these for a different admin to approve. On approval the grant is applied.
/// </summary>
[Audited]
public class PermissionGrantChangeRequest : Entity<Guid>, IMultiTenant
{
    public Guid? TenantId { get; private set; }
    public Guid ScopeTenantId { get; private set; }

    public string PermissionName { get; private set; } = default!;

    public string ProviderName { get; private set; } = default!;

    public string ProviderKey { get; private set; } = default!;

    public PermissionGrantEffect RequestedEffect { get; private set; }

    public DateTime? ExpiresAtUtc { get; private set; }

    public string? Reason { get; private set; }

    public string? TicketId { get; private set; }

    public Guid? RequestedByUserId { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public PermissionGrantChangeRequestStatus Status { get; private set; } = PermissionGrantChangeRequestStatus.Pending;

    public Guid? DecidedByUserId { get; private set; }

    public string? DecisionReason { get; private set; }

    public DateTime? DecidedAtUtc { get; private set; }

    public long Version { get; private set; } = 1;
    public PermissionGrantScopeKind GrantScopeKind { get; private set; } = PermissionGrantScopeKind.Provider;
    public PermissionDelegationScopeKinds ResourceScopeKind { get; private set; } = PermissionDelegationScopeKinds.None;
    public string? OwnerServiceName { get; private set; }
    public string? ResourceName { get; private set; }
    public string? ResourceKey { get; private set; }
    public long? OwnerRevision { get; private set; }
    public Guid? OwnerValidationReceiptId { get; private set; }
    public long? OwnerValidationReceiptRevision { get; private set; }
    public DateTime? OwnerValidationReceiptExpiresAtUtc { get; private set; }
    public Guid? DelegationDefinitionVersionId { get; private set; }
    public Guid? ExpectedResourceAssignmentVersion { get; private set; }
    public Guid? ResourceAssignmentOperationId { get; private set; }
    public ResourceAccessReviewState ReviewState { get; private set; } = ResourceAccessReviewState.NotRequired;
    public DateTime? ReviewDueAtUtc { get; private set; }

    protected PermissionGrantChangeRequest()
    {
    }

    public PermissionGrantChangeRequest(
        Guid id,
        Guid? tenantId,
        string permissionName,
        string providerName,
        string providerKey,
        PermissionGrantEffect requestedEffect,
        DateTime? expiresAtUtc,
        string? reason,
        string? ticketId,
        Guid? requestedByUserId,
        DateTime createdAtUtc)
        : base(id)
    {
        TenantId = tenantId;
        ScopeTenantId = tenantId ?? Guid.Empty;
        PermissionName = Check.NotNullOrWhiteSpace(permissionName, nameof(permissionName), maxLength: PermissionsConsts.MaxPermissionNameLength);
        ProviderName = Check.NotNullOrWhiteSpace(providerName, nameof(providerName), maxLength: PermissionsConsts.MaxProviderNameLength);
        ProviderKey = Check.NotNullOrWhiteSpace(providerKey, nameof(providerKey), maxLength: PermissionsConsts.MaxProviderKeyLength);
        RequestedEffect = requestedEffect;
        ExpiresAtUtc = expiresAtUtc;
        Reason = Normalize(reason, PermissionGrantConsts.MaxReasonLength);
        TicketId = Normalize(ticketId, PermissionGrantConsts.MaxTicketIdLength);
        RequestedByUserId = requestedByUserId;
        CreatedAtUtc = createdAtUtc;
        Status = PermissionGrantChangeRequestStatus.Pending;
        Version = 1;
    }

    /// <summary>Marks the request approved+applied. Applying the grant is done by the caller.</summary>
    public void Approve(Guid? decidedByUserId, string? decisionReason, DateTime decidedAtUtc)
    {
        EnsurePending();
        Status = PermissionGrantChangeRequestStatus.Applied;
        SetDecision(decidedByUserId, decisionReason, decidedAtUtc);
    }

    public void Reject(Guid? decidedByUserId, string? decisionReason, DateTime decidedAtUtc)
    {
        EnsurePending();
        Status = PermissionGrantChangeRequestStatus.Rejected;
        SetDecision(decidedByUserId, decisionReason, decidedAtUtc);
    }

    public void Cancel(Guid? decidedByUserId, string? decisionReason, DateTime decidedAtUtc)
    {
        EnsurePending();
        Status = PermissionGrantChangeRequestStatus.Cancelled;
        SetDecision(decidedByUserId, decisionReason, decidedAtUtc);
    }

    public void BindOwnerValidatedResourceScope(PermissionDelegationScopeKinds resourceScopeKind, string ownerServiceName,
        string resourceName, string resourceKey, long ownerRevision, Guid receiptId, long receiptRevision,
        DateTime receiptExpiresAtUtc, Guid delegationDefinitionVersionId, Guid expectedAssignmentVersion, Guid resourceAssignmentOperationId,
        ResourceAccessReviewState reviewState, DateTime? reviewDueAtUtc)
    {
        if (resourceScopeKind == PermissionDelegationScopeKinds.None || (resourceScopeKind & PermissionDelegationScopeKinds.Resource) == 0 ||
            ownerRevision <= 0 || receiptId == Guid.Empty || receiptRevision <= 0 || delegationDefinitionVersionId == Guid.Empty || resourceAssignmentOperationId == Guid.Empty)
            throw new ArgumentException("A complete owner-validated resource approval context is required.");
        if (receiptExpiresAtUtc.Kind != DateTimeKind.Utc || receiptExpiresAtUtc <= CreatedAtUtc)
            throw new ArgumentException("The owner-validation receipt must be a future UTC instant.", nameof(receiptExpiresAtUtc));
        if ((reviewState == ResourceAccessReviewState.NotRequired) == reviewDueAtUtc.HasValue)
            throw new ArgumentException("A review due instant is required exactly when the request has a review lifecycle.", nameof(reviewDueAtUtc));
        GrantScopeKind = PermissionGrantScopeKind.Resource; ResourceScopeKind = resourceScopeKind;
        OwnerServiceName = Check.NotNullOrWhiteSpace(ownerServiceName, nameof(ownerServiceName), 64);
        ResourceName = Check.NotNullOrWhiteSpace(resourceName, nameof(resourceName), PermissionGrantConsts.MaxResourceNameLength);
        ResourceKey = Check.NotNullOrWhiteSpace(resourceKey, nameof(resourceKey), PermissionGrantConsts.MaxResourceKeyLength);
        OwnerRevision = ownerRevision; OwnerValidationReceiptId = receiptId; OwnerValidationReceiptRevision = receiptRevision;
        OwnerValidationReceiptExpiresAtUtc = receiptExpiresAtUtc; DelegationDefinitionVersionId = delegationDefinitionVersionId;
        ExpectedResourceAssignmentVersion = expectedAssignmentVersion; ReviewState = reviewState;
        ResourceAssignmentOperationId = resourceAssignmentOperationId;
        ReviewDueAtUtc = reviewDueAtUtc.HasValue ? DateTime.SpecifyKind(reviewDueAtUtc.Value, DateTimeKind.Utc) : null;
    }

    private void SetDecision(Guid? decidedByUserId, string? decisionReason, DateTime decidedAtUtc)
    {
        DecidedByUserId = decidedByUserId;
        DecisionReason = Normalize(decisionReason, PermissionGrantConsts.MaxReasonLength);
        DecidedAtUtc = decidedAtUtc;
        Version++;
    }

    private void EnsurePending()
    {
        if (Status != PermissionGrantChangeRequestStatus.Pending)
        {
            throw new InvalidOperationException($"Grant change request is already {Status} and cannot be modified.");
        }
    }

    private static string? Normalize(string? value, int maxLength)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : Check.NotNullOrWhiteSpace(value, nameof(value), maxLength: maxLength);
    }
}
