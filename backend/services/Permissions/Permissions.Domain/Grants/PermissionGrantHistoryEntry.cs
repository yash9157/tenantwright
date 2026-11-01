using Permissions.Domain.Shared.Constants;
using Permissions.Domain.Shared.Enums;
using Shared;
using Shared.Domain;
using Shared.Domain.Entities;

namespace Permissions.Domain.Grants;

/// <summary>Append-only history of provider and typed resource permission grant changes.</summary>
public class PermissionGrantHistoryEntry : Entity<Guid>, IMultiTenant
{
    public Guid? TenantId { get; private set; }
    public string ProviderName { get; private set; } = default!;
    public string ProviderKey { get; private set; } = default!;
    public string PermissionName { get; private set; } = default!;
    public string? ResourceName { get; private set; }
    public string? ResourceKey { get; private set; }
    public long? ResourceOwnerRevision { get; private set; }
    public PermissionGrantHistoryChangeType ChangeType { get; private set; }
    public PermissionGrantEffect? OldEffect { get; private set; }
    public PermissionGrantEffect? NewEffect { get; private set; }
    public DateTime? ExpiresAtUtc { get; private set; }
    public string? Reason { get; private set; }
    public string? TicketId { get; private set; }
    public Guid? ActorUserId { get; private set; }
    public DateTime OccurredAtUtc { get; private set; }
    public PermissionGrantScopeKind GrantScopeKind { get; private set; } = PermissionGrantScopeKind.Provider;
    public PermissionDelegationScopeKinds ResourceScopeKind { get; private set; } = PermissionDelegationScopeKinds.None;
    public string? OwnerServiceName { get; private set; }
    public Guid? DelegationDefinitionVersionId { get; private set; }
    public Guid? OwnerValidationReceiptId { get; private set; }
    public long? OwnerValidationReceiptRevision { get; private set; }
    public DateTime? OwnerValidationReceiptExpiresAtUtc { get; private set; }
    public Guid? ApprovalRequestId { get; private set; }
    public Guid? ApprovalRequestScopeTenantId { get; private set; }
    public ResourceAccessReviewState ReviewState { get; private set; } = ResourceAccessReviewState.NotRequired;

    protected PermissionGrantHistoryEntry()
    {
    }

    public void BindOwnerValidatedResourceScope(PermissionDelegationScopeKinds scopeKind, string ownerServiceName,
        Guid delegationDefinitionVersionId, Guid receiptId, long receiptRevision, DateTime receiptExpiresAtUtc,
        Guid? approvalRequestId, ResourceAccessReviewState reviewState)
    {
        if (ResourceName == null || ResourceKey == null || ResourceOwnerRevision is not > 0 ||
            scopeKind == PermissionDelegationScopeKinds.None || (scopeKind & PermissionDelegationScopeKinds.Resource) == 0 ||
            delegationDefinitionVersionId == Guid.Empty || receiptId == Guid.Empty || receiptRevision <= 0)
            throw new InvalidOperationException("Resource history requires the complete owner-validated scope.");
        if (receiptExpiresAtUtc.Kind != DateTimeKind.Utc || receiptExpiresAtUtc <= OccurredAtUtc)
            throw new ArgumentException("The owner-validation receipt must be a future UTC instant.", nameof(receiptExpiresAtUtc));
        GrantScopeKind = PermissionGrantScopeKind.Resource; ResourceScopeKind = scopeKind;
        OwnerServiceName = Check.NotNullOrWhiteSpace(ownerServiceName, nameof(ownerServiceName), 64);
        DelegationDefinitionVersionId = delegationDefinitionVersionId; OwnerValidationReceiptId = receiptId;
        OwnerValidationReceiptRevision = receiptRevision; OwnerValidationReceiptExpiresAtUtc = receiptExpiresAtUtc;
        if (approvalRequestId.HasValue && !TenantId.HasValue)
            throw new InvalidOperationException("Resource approval history requires a tenant-qualified approval request.");
        ApprovalRequestId = approvalRequestId;
        ApprovalRequestScopeTenantId = approvalRequestId.HasValue ? TenantId : null;
        ReviewState = reviewState;
    }

    public PermissionGrantHistoryEntry(
        Guid id,
        Guid? tenantId,
        string providerName,
        string providerKey,
        string permissionName,
        string? resourceName,
        string? resourceKey,
        long? resourceOwnerRevision,
        PermissionGrantHistoryChangeType changeType,
        PermissionGrantEffect? oldEffect,
        PermissionGrantEffect? newEffect,
        DateTime? expiresAtUtc,
        string? reason,
        string? ticketId,
        Guid? actorUserId,
        DateTime occurredAtUtc)
        : base(id)
    {
        var hasResourceScope = !string.IsNullOrWhiteSpace(resourceName) ||
                               !string.IsNullOrWhiteSpace(resourceKey) ||
                               resourceOwnerRevision.HasValue;
        if (hasResourceScope &&
            (string.IsNullOrWhiteSpace(resourceName) || string.IsNullOrWhiteSpace(resourceKey) ||
             resourceOwnerRevision is null or <= 0))
            throw new ArgumentException("Typed resource history requires resource name, key, and positive owner revision.");

        TenantId = tenantId;
        ProviderName = Check.NotNullOrWhiteSpace(providerName, nameof(providerName), maxLength: PermissionsConsts.MaxProviderNameLength);
        ProviderKey = Check.NotNullOrWhiteSpace(providerKey, nameof(providerKey), maxLength: PermissionsConsts.MaxProviderKeyLength);
        PermissionName = Check.NotNullOrWhiteSpace(permissionName, nameof(permissionName), maxLength: PermissionsConsts.MaxPermissionNameLength);
        ResourceName = hasResourceScope
            ? Check.NotNullOrWhiteSpace(resourceName!, nameof(resourceName), maxLength: PermissionGrantConsts.MaxResourceNameLength)
            : null;
        ResourceKey = hasResourceScope
            ? Check.NotNullOrWhiteSpace(resourceKey!, nameof(resourceKey), maxLength: PermissionGrantConsts.MaxResourceKeyLength)
            : null;
        ResourceOwnerRevision = resourceOwnerRevision;
        ChangeType = changeType;
        OldEffect = oldEffect;
        NewEffect = newEffect;
        ExpiresAtUtc = expiresAtUtc;
        Reason = string.IsNullOrWhiteSpace(reason) ? null : Check.NotNullOrWhiteSpace(reason.Trim(), nameof(reason), maxLength: PermissionGrantConsts.MaxReasonLength);
        TicketId = string.IsNullOrWhiteSpace(ticketId) ? null : Check.NotNullOrWhiteSpace(ticketId.Trim(), nameof(ticketId), maxLength: PermissionGrantConsts.MaxTicketIdLength);
        ActorUserId = actorUserId;
        OccurredAtUtc = occurredAtUtc.Kind == DateTimeKind.Utc
            ? occurredAtUtc
            : throw new ArgumentException("History occurrence time must be UTC.", nameof(occurredAtUtc));
    }
}
