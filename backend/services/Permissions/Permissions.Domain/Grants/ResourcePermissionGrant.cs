using Permissions.Domain.Shared.Constants;
using Permissions.Domain.Shared.Enums;
using Shared;
using Shared.AuditLogs.Auditing;
using Shared.Domain;
using Shared.Domain.Entities;

namespace Permissions.Domain.Grants;

[Audited]
public class ResourcePermissionGrant : Entity<Guid>, IMultiTenant
{
    public Guid? TenantId { get; private set; }
    public string Name { get; private set; } = default!;
    public string ProviderName { get; private set; } = default!;
    public string ProviderKey { get; private set; } = default!;
    public string ResourceName { get; private set; } = default!;
    public string ResourceKey { get; private set; } = default!;
    public long OwnerRevision { get; private set; }
    public PermissionGrantEffect Effect { get; private set; }
    public DateTime CreationTimeUtc { get; private set; }
    public DateTime? ExpiresAtUtc { get; private set; }
    public string? Reason { get; private set; }
    public string? TicketId { get; private set; }
    public Guid? GrantedByUserId { get; private set; }
    public PermissionGrantScopeKind GrantScopeKind { get; private set; } = PermissionGrantScopeKind.Provider;
    public PermissionDelegationScopeKinds ResourceScopeKind { get; private set; } = PermissionDelegationScopeKinds.None;
    public string? OwnerServiceName { get; private set; }
    public Guid? DelegationDefinitionVersionId { get; private set; }
    public Guid? OwnerValidationReceiptId { get; private set; }
    public long? OwnerValidationReceiptRevision { get; private set; }
    public DateTime? OwnerValidationReceiptExpiresAtUtc { get; private set; }
    public ResourceAccessApprovalState ApprovalState { get; private set; } = ResourceAccessApprovalState.NotRequired;
    public ResourceAccessReviewState ReviewState { get; private set; } = ResourceAccessReviewState.NotRequired;
    public DateTime? ReviewDueAtUtc { get; private set; }
    public DateTime? LastReviewedAtUtc { get; private set; }

    protected ResourcePermissionGrant()
    {
    }

    public ResourcePermissionGrant(
        Guid id,
        Guid? tenantId,
        string name,
        string resourceName,
        string resourceKey,
        long ownerRevision,
        string providerName,
        string providerKey,
        PermissionGrantEffect effect,
        DateTime creationTimeUtc,
        DateTime? expiresAtUtc,
        string? reason,
        string? ticketId,
        Guid? grantedByUserId)
        : base(id)
    {
        if (ownerRevision <= 0)
            throw new ArgumentOutOfRangeException(nameof(ownerRevision), "Owner revision must be positive.");

        TenantId = tenantId;
        Name = Check.NotNullOrWhiteSpace(name, nameof(name), maxLength: PermissionDefinitionRecordConsts.MaxNameLength);
        ResourceName = Check.NotNullOrWhiteSpace(resourceName, nameof(resourceName), maxLength: PermissionGrantConsts.MaxResourceNameLength);
        ResourceKey = Check.NotNullOrWhiteSpace(resourceKey, nameof(resourceKey), maxLength: PermissionGrantConsts.MaxResourceKeyLength);
        ProviderName = Check.NotNullOrWhiteSpace(providerName, nameof(providerName), maxLength: PermissionGrantConsts.MaxProviderNameLength);
        ProviderKey = Check.NotNullOrWhiteSpace(providerKey, nameof(providerKey), maxLength: PermissionGrantConsts.MaxProviderKeyLength);
        OwnerRevision = ownerRevision;
        Effect = effect;
        CreationTimeUtc = EnsureUtc(creationTimeUtc, nameof(creationTimeUtc));
        SetLifecycleMetadata(ownerRevision, expiresAtUtc, reason, ticketId, grantedByUserId);
    }

    public void Update(
        long ownerRevision,
        PermissionGrantEffect effect,
        DateTime? expiresAtUtc,
        string? reason,
        string? ticketId,
        Guid? grantedByUserId)
    {
        if (ownerRevision <= 0)
            throw new ArgumentOutOfRangeException(nameof(ownerRevision), "Owner revision must be positive.");

        OwnerRevision = ownerRevision;
        Effect = effect;
        SetLifecycleMetadata(ownerRevision, expiresAtUtc, reason, ticketId, grantedByUserId);
    }

    public bool IsExpired(DateTime utcNow) => ExpiresAtUtc.HasValue && ExpiresAtUtc.Value <= utcNow;

    public void BindOwnerValidatedResourceScope(
        PermissionDelegationScopeKinds resourceScopeKind, string ownerServiceName, Guid delegationDefinitionVersionId,
        Guid ownerValidationReceiptId, long ownerValidationReceiptRevision, DateTime ownerValidationReceiptExpiresAtUtc,
        ResourceAccessApprovalState approvalState, ResourceAccessReviewState reviewState, DateTime? reviewDueAtUtc,
        DateTime? lastReviewedAtUtc)
    {
        if (resourceScopeKind == PermissionDelegationScopeKinds.None ||
            (resourceScopeKind & PermissionDelegationScopeKinds.Resource) == 0)
            throw new ArgumentException("An owner-validated resource scope is required.", nameof(resourceScopeKind));
        OwnerServiceName = Check.NotNullOrWhiteSpace(ownerServiceName, nameof(ownerServiceName), 64);
        if (delegationDefinitionVersionId == Guid.Empty || ownerValidationReceiptId == Guid.Empty || ownerValidationReceiptRevision <= 0)
            throw new ArgumentException("A descriptor and positive owner-validation receipt are required.");
        var receiptExpiry = EnsureUtc(ownerValidationReceiptExpiresAtUtc, nameof(ownerValidationReceiptExpiresAtUtc));
        if (receiptExpiry <= CreationTimeUtc) throw new ArgumentException("The owner-validation receipt must outlive the grant creation.", nameof(ownerValidationReceiptExpiresAtUtc));
        if (approvalState is ResourceAccessApprovalState.Pending or ResourceAccessApprovalState.Rejected)
            throw new InvalidOperationException("Only approved effective grants may be stored.");
        if ((reviewState == ResourceAccessReviewState.NotRequired) == reviewDueAtUtc.HasValue)
            throw new ArgumentException("A review due instant is required exactly when the grant has a review lifecycle.", nameof(reviewDueAtUtc));
        GrantScopeKind = PermissionGrantScopeKind.Resource;
        ResourceScopeKind = resourceScopeKind;
        DelegationDefinitionVersionId = delegationDefinitionVersionId;
        OwnerValidationReceiptId = ownerValidationReceiptId;
        OwnerValidationReceiptRevision = ownerValidationReceiptRevision;
        OwnerValidationReceiptExpiresAtUtc = receiptExpiry;
        ApprovalState = approvalState;
        ReviewState = reviewState;
        ReviewDueAtUtc = reviewDueAtUtc.HasValue ? EnsureUtc(reviewDueAtUtc.Value, nameof(reviewDueAtUtc)) : null;
        LastReviewedAtUtc = lastReviewedAtUtc.HasValue ? EnsureUtc(lastReviewedAtUtc.Value, nameof(lastReviewedAtUtc)) : null;
    }

    private void SetLifecycleMetadata(
        long ownerRevision,
        DateTime? expiresAtUtc,
        string? reason,
        string? ticketId,
        Guid? grantedByUserId)
    {
        if (expiresAtUtc.HasValue)
        {
            expiresAtUtc = EnsureUtc(expiresAtUtc.Value, nameof(expiresAtUtc));
            if (expiresAtUtc <= CreationTimeUtc)
                throw new ArgumentOutOfRangeException(nameof(expiresAtUtc), "Grant expiry must be after creation.");
        }

        OwnerRevision = ownerRevision;
        ExpiresAtUtc = expiresAtUtc;
        Reason = string.IsNullOrWhiteSpace(reason)
            ? null
            : Check.NotNullOrWhiteSpace(reason.Trim(), nameof(reason), maxLength: PermissionGrantConsts.MaxReasonLength);
        TicketId = string.IsNullOrWhiteSpace(ticketId)
            ? null
            : Check.NotNullOrWhiteSpace(ticketId.Trim(), nameof(ticketId), maxLength: PermissionGrantConsts.MaxTicketIdLength);
        GrantedByUserId = grantedByUserId;
    }

    private static DateTime EnsureUtc(DateTime value, string parameterName)
    {
        if (value.Kind != DateTimeKind.Utc)
            throw new ArgumentException("The timestamp must be UTC.", parameterName);
        return value;
    }
}
