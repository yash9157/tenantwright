using Permissions.Domain.Shared.Constants;
using Permissions.Domain.Shared.Enums;
using Shared;
using Shared.Domain;
using Shared.Domain.Entities;

namespace Permissions.Domain.Grants;

public sealed class ResourcePermissionAssignmentState : Entity<Guid>, IMultiTenant
{
    public Guid? TenantId { get; private set; }
    public string ResourceName { get; private set; } = default!;
    public string ResourceKey { get; private set; } = default!;
    public string ProviderName { get; private set; } = default!;
    public string ProviderKey { get; private set; } = default!;
    public Guid Version { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }
    public PermissionDelegationScopeKinds ResourceScopeKind { get; private set; } = PermissionDelegationScopeKinds.None;
    public string? OwnerServiceName { get; private set; }
    public long? OwnerRevision { get; private set; }
    public Guid? OwnerValidationReceiptId { get; private set; }
    public long? OwnerValidationReceiptRevision { get; private set; }
    public DateTime? OwnerValidationReceiptExpiresAtUtc { get; private set; }
    public long? EffectiveScopeRevision { get; private set; }

    private ResourcePermissionAssignmentState() { }

    public ResourcePermissionAssignmentState(
        Guid id,
        Guid tenantId,
        string resourceName,
        string resourceKey,
        string providerName,
        string providerKey,
        DateTime utcNow)
        : base(id)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("A tenant is required.", nameof(tenantId));

        TenantId = tenantId;
        ResourceName = Check.NotNullOrWhiteSpace(resourceName, nameof(resourceName), maxLength: PermissionGrantConsts.MaxResourceNameLength);
        ResourceKey = Check.NotNullOrWhiteSpace(resourceKey, nameof(resourceKey), maxLength: PermissionGrantConsts.MaxResourceKeyLength);
        ProviderName = Check.NotNullOrWhiteSpace(providerName, nameof(providerName), maxLength: PermissionGrantConsts.MaxProviderNameLength);
        ProviderKey = Check.NotNullOrWhiteSpace(providerKey, nameof(providerKey), maxLength: PermissionGrantConsts.MaxProviderKeyLength);
        Advance(utcNow);
    }

    public void Advance(DateTime utcNow)
    {
        if (utcNow.Kind != DateTimeKind.Utc)
            throw new ArgumentException("The timestamp must be UTC.", nameof(utcNow));
        Version = Guid.CreateVersion7();
        UpdatedAtUtc = utcNow;
    }

    public void BindOwnerValidation(PermissionDelegationScopeKinds resourceScopeKind, string ownerServiceName,
        long ownerRevision, Guid receiptId, long receiptRevision, DateTime receiptExpiresAtUtc, long effectiveScopeRevision)
    {
        if (resourceScopeKind == PermissionDelegationScopeKinds.None || (resourceScopeKind & PermissionDelegationScopeKinds.Resource) == 0 ||
            ownerRevision <= 0 || receiptId == Guid.Empty || receiptRevision <= 0 || effectiveScopeRevision <= 0)
            throw new ArgumentException("A complete positive owner-validation context is required.");
        if (receiptExpiresAtUtc.Kind != DateTimeKind.Utc || receiptExpiresAtUtc <= UpdatedAtUtc)
            throw new ArgumentException("The owner-validation receipt must be a future UTC instant.", nameof(receiptExpiresAtUtc));
        ResourceScopeKind = resourceScopeKind;
        OwnerServiceName = Check.NotNullOrWhiteSpace(ownerServiceName, nameof(ownerServiceName), 64);
        OwnerRevision = ownerRevision; OwnerValidationReceiptId = receiptId; OwnerValidationReceiptRevision = receiptRevision;
        OwnerValidationReceiptExpiresAtUtc = receiptExpiresAtUtc; EffectiveScopeRevision = effectiveScopeRevision;
    }
}
