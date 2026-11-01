using Shared;
using Shared.Domain;
using Shared.Domain.Entities;
using Permissions.Domain.Shared.Enums;

namespace Permissions.Domain.Grants;

public sealed class PermissionApprovalDecisionOperation : Entity<Guid>, IMultiTenant
{
    public Guid? TenantId { get; private set; }
    public Guid ActorUserId { get; private set; }
    public string IdempotencyKey { get; private set; } = default!;
    public string RequestHash { get; private set; } = default!;
    public string ResultJson { get; private set; } = default!;
    public DateTime CreatedAtUtc { get; private set; }
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

    private PermissionApprovalDecisionOperation() { }

    public PermissionApprovalDecisionOperation(
        Guid id,
        Guid? tenantId,
        Guid actorUserId,
        string idempotencyKey,
        string requestHash,
        string resultJson,
        DateTime createdAtUtc) : base(id)
    {
        TenantId = tenantId;
        ActorUserId = actorUserId;
        IdempotencyKey = Check.NotNullOrWhiteSpace(idempotencyKey, nameof(idempotencyKey), 128);
        RequestHash = Check.NotNullOrWhiteSpace(requestHash, nameof(requestHash), 64);
        ResultJson = Check.NotNullOrWhiteSpace(resultJson, nameof(resultJson));
        CreatedAtUtc = createdAtUtc;
    }

    public void BindOwnerValidatedResourceScope(PermissionDelegationScopeKinds scopeKind, string ownerServiceName,
        string resourceName, string resourceKey, long ownerRevision, Guid receiptId, long receiptRevision,
        DateTime receiptExpiresAtUtc, Guid delegationDefinitionVersionId)
    {
        if (scopeKind == PermissionDelegationScopeKinds.None || (scopeKind & PermissionDelegationScopeKinds.Resource) == 0 ||
            ownerRevision <= 0 || receiptId == Guid.Empty || receiptRevision <= 0 || delegationDefinitionVersionId == Guid.Empty)
            throw new ArgumentException("A complete owner-validated resource decision context is required.");
        if (receiptExpiresAtUtc.Kind != DateTimeKind.Utc || receiptExpiresAtUtc <= CreatedAtUtc)
            throw new ArgumentException("The owner-validation receipt must be a future UTC instant.", nameof(receiptExpiresAtUtc));
        GrantScopeKind = PermissionGrantScopeKind.Resource; ResourceScopeKind = scopeKind;
        OwnerServiceName = Check.NotNullOrWhiteSpace(ownerServiceName, nameof(ownerServiceName), 64);
        ResourceName = Check.NotNullOrWhiteSpace(resourceName, nameof(resourceName), 256);
        ResourceKey = Check.NotNullOrWhiteSpace(resourceKey, nameof(resourceKey), 256);
        OwnerRevision = ownerRevision; OwnerValidationReceiptId = receiptId; OwnerValidationReceiptRevision = receiptRevision;
        OwnerValidationReceiptExpiresAtUtc = receiptExpiresAtUtc; DelegationDefinitionVersionId = delegationDefinitionVersionId;
    }
}
