using Permissions.Domain.Shared.Constants;
using Permissions.Domain.Shared.Enums;
using Shared;
using Shared.Domain;
using Shared.Domain.Entities;

namespace Permissions.Domain.Grants;

public sealed class ResourcePermissionAssignmentOperation : Entity<Guid>, IMultiTenant
{
    public Guid? TenantId { get; private set; }
    public Guid ScopeTenantId { get; private set; }
    public Guid ActorUserId { get; private set; }
    public string ResourceName { get; private set; } = default!;
    public string ResourceKey { get; private set; } = default!;
    public string ProviderName { get; private set; } = default!;
    public string ProviderKey { get; private set; } = default!;
    public string IdempotencyKey { get; private set; } = default!;
    public string RequestHash { get; private set; } = default!;
    public PermissionAssignmentOperationStatus Status { get; private set; }
    public string? ResultPayload { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? CompletedAtUtc { get; private set; }
    public PermissionDelegationScopeKinds ResourceScopeKind { get; private set; } = PermissionDelegationScopeKinds.None;
    public string? OwnerServiceName { get; private set; }
    public long? OwnerRevision { get; private set; }
    public Guid? OwnerValidationReceiptId { get; private set; }
    public long? OwnerValidationReceiptRevision { get; private set; }
    public DateTime? OwnerValidationReceiptExpiresAtUtc { get; private set; }

    private ResourcePermissionAssignmentOperation() { }

    public ResourcePermissionAssignmentOperation(
        Guid id,
        Guid tenantId,
        Guid actorUserId,
        string resourceName,
        string resourceKey,
        string providerName,
        string providerKey,
        string idempotencyKey,
        string requestHash,
        DateTime createdAtUtc)
        : base(id)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("A tenant is required.", nameof(tenantId));
        if (actorUserId == Guid.Empty) throw new ArgumentException("An actor is required.", nameof(actorUserId));
        if (createdAtUtc.Kind != DateTimeKind.Utc) throw new ArgumentException("The timestamp must be UTC.", nameof(createdAtUtc));

        TenantId = tenantId;
        ScopeTenantId = tenantId;
        ActorUserId = actorUserId;
        ResourceName = Check.NotNullOrWhiteSpace(resourceName, nameof(resourceName), maxLength: PermissionGrantConsts.MaxResourceNameLength);
        ResourceKey = Check.NotNullOrWhiteSpace(resourceKey, nameof(resourceKey), maxLength: PermissionGrantConsts.MaxResourceKeyLength);
        ProviderName = Check.NotNullOrWhiteSpace(providerName, nameof(providerName), maxLength: PermissionGrantConsts.MaxProviderNameLength);
        ProviderKey = Check.NotNullOrWhiteSpace(providerKey, nameof(providerKey), maxLength: PermissionGrantConsts.MaxProviderKeyLength);
        IdempotencyKey = Check.NotNullOrWhiteSpace(idempotencyKey, nameof(idempotencyKey), maxLength: ResourcePermissionAssignmentConsts.MaxIdempotencyKeyLength);
        RequestHash = Check.NotNullOrWhiteSpace(requestHash, nameof(requestHash), maxLength: ResourcePermissionAssignmentConsts.MaxRequestHashLength);
        Status = PermissionAssignmentOperationStatus.InProgress;
        CreatedAtUtc = createdAtUtc;
    }

    public void Complete(string resultPayload, DateTime completedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(resultPayload)) throw new ArgumentException("A result is required.", nameof(resultPayload));
        if (completedAtUtc.Kind != DateTimeKind.Utc) throw new ArgumentException("The timestamp must be UTC.", nameof(completedAtUtc));
        ResultPayload = resultPayload;
        Status = PermissionAssignmentOperationStatus.Completed;
        CompletedAtUtc = completedAtUtc;
    }

    public void StorePendingResult(string resultPayload)
    {
        if (Status != PermissionAssignmentOperationStatus.InProgress)
            throw new InvalidOperationException("Only an in-progress assignment operation can become pending approval.");
        if (string.IsNullOrWhiteSpace(resultPayload))
            throw new ArgumentException("A pending result is required.", nameof(resultPayload));
        ResultPayload = resultPayload;
    }

    public void BindOwnerValidation(PermissionDelegationScopeKinds resourceScopeKind, string ownerServiceName,
        long ownerRevision, Guid receiptId, long receiptRevision, DateTime receiptExpiresAtUtc)
    {
        if (resourceScopeKind == PermissionDelegationScopeKinds.None || (resourceScopeKind & PermissionDelegationScopeKinds.Resource) == 0 ||
            ownerRevision <= 0 || receiptId == Guid.Empty || receiptRevision <= 0)
            throw new ArgumentException("A complete positive owner-validation context is required.");
        if (receiptExpiresAtUtc.Kind != DateTimeKind.Utc || receiptExpiresAtUtc <= CreatedAtUtc)
            throw new ArgumentException("The owner-validation receipt must be a future UTC instant.", nameof(receiptExpiresAtUtc));
        ResourceScopeKind = resourceScopeKind;
        OwnerServiceName = Check.NotNullOrWhiteSpace(ownerServiceName, nameof(ownerServiceName), 64);
        OwnerRevision = ownerRevision; OwnerValidationReceiptId = receiptId; OwnerValidationReceiptRevision = receiptRevision;
        OwnerValidationReceiptExpiresAtUtc = receiptExpiresAtUtc;
    }
}
