using Permissions.Domain.Shared.Enums;
using Shared;
using Shared.AuditLogs.Auditing;
using Shared.Domain.Entities.Auditing;

namespace Permissions.Domain.PermissionManagement;

[Audited]
public sealed class PermissionDelegationDefinition : FullAuditedAggregateRoot<Guid>
{
    public string PermissionName { get; private set; } = default!;
    public string OwningService { get; private set; } = default!;
    public PermissionDelegationDefinitionLifecycle Lifecycle { get; private set; }
    public Guid? CurrentVersionId { get; private set; }

    private PermissionDelegationDefinition() { }

    public PermissionDelegationDefinition(Guid id, string permissionName, string owningService)
        : base(PermissionDelegationDefinitionGuards.RequireUuid7(id, nameof(id)))
    {
        PermissionName = Check.NotNullOrWhiteSpace(permissionName, nameof(permissionName), 256);
        OwningService = Check.NotNullOrWhiteSpace(owningService, nameof(owningService), 64);
        Lifecycle = PermissionDelegationDefinitionLifecycle.Active;
    }

    public void Publish(Guid versionId)
    {
        if (Lifecycle != PermissionDelegationDefinitionLifecycle.Active)
            throw new InvalidOperationException("A retired delegation definition cannot publish a version.");
        CurrentVersionId = versionId == Guid.Empty ? throw new ArgumentException("A version is required.", nameof(versionId)) : versionId;
    }

    public void Retire() => Lifecycle = PermissionDelegationDefinitionLifecycle.Retired;
}

[Audited]
public sealed class PermissionDelegationDefinitionVersion : FullAuditedAggregateRoot<Guid>
{
    public Guid DefinitionId { get; private set; }
    public int Version { get; private set; }
    public PermissionDelegationDefinitionPublicationState PublicationState { get; private set; }
    public string SourceDefinitionChecksum { get; private set; } = default!;
    public string ManagerPermissionName { get; private set; } = default!;
    public PermissionDelegationTargetKinds AllowedTargetKinds { get; private set; }
    public PermissionDelegationScopeKinds AllowedScopeKinds { get; private set; }
    public PermissionDelegationEffects AllowedEffects { get; private set; }
    public int? MaximumExpirySeconds { get; private set; }
    public bool RequiresApproval { get; private set; }
    public string? ApproverPermissionName { get; private set; }
    public short? ReviewCadenceDays { get; private set; }
    public string? RequiredFeatureName { get; private set; }
    public bool Delegable { get; private set; }
    public DateTime EffectiveFromUtc { get; private set; }
    public DateTime? EffectiveToUtc { get; private set; }

    private PermissionDelegationDefinitionVersion() { }

    public PermissionDelegationDefinitionVersion(Guid id, Guid definitionId, int version,
        string sourceDefinitionChecksum, string managerPermissionName,
        PermissionDelegationTargetKinds allowedTargetKinds, PermissionDelegationScopeKinds allowedScopeKinds,
        PermissionDelegationEffects allowedEffects, int? maximumExpirySeconds, bool requiresApproval,
        string? approverPermissionName, short? reviewCadenceDays, string? requiredFeatureName,
        bool delegable, DateTime effectiveFromUtc, DateTime? effectiveToUtc)
        : base(PermissionDelegationDefinitionGuards.RequireUuid7(id, nameof(id)))
    {
        DefinitionId = definitionId == Guid.Empty ? throw new ArgumentException("A definition is required.", nameof(definitionId)) : definitionId;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(version);
        Version = version;
        SourceDefinitionChecksum = Check.NotNullOrWhiteSpace(sourceDefinitionChecksum, nameof(sourceDefinitionChecksum), 64);
        if (SourceDefinitionChecksum.Length != 64 || !SourceDefinitionChecksum.All(Uri.IsHexDigit))
            throw new ArgumentException("The source checksum must be a hexadecimal SHA-256 value.", nameof(sourceDefinitionChecksum));
        ManagerPermissionName = Check.NotNullOrWhiteSpace(managerPermissionName, nameof(managerPermissionName), 256);
        const PermissionDelegationTargetKinds knownTargets = PermissionDelegationTargetKinds.User | PermissionDelegationTargetKinds.Role;
        const PermissionDelegationScopeKinds knownScopes = PermissionDelegationScopeKinds.Tenant | PermissionDelegationScopeKinds.Resource;
        const PermissionDelegationEffects knownEffects = PermissionDelegationEffects.Allow | PermissionDelegationEffects.Deny;
        if (allowedTargetKinds == PermissionDelegationTargetKinds.None || (allowedTargetKinds & ~knownTargets) != 0 ||
            allowedScopeKinds == PermissionDelegationScopeKinds.None || (allowedScopeKinds & ~knownScopes) != 0 ||
            allowedEffects == PermissionDelegationEffects.None || (allowedEffects & ~knownEffects) != 0)
            throw new ArgumentException("A delegation version must declare targets, scopes, and effects.");
        AllowedTargetKinds = allowedTargetKinds; AllowedScopeKinds = allowedScopeKinds; AllowedEffects = allowedEffects;
        if (maximumExpirySeconds is <= 0) throw new ArgumentOutOfRangeException(nameof(maximumExpirySeconds));
        MaximumExpirySeconds = maximumExpirySeconds;
        RequiresApproval = requiresApproval;
        ApproverPermissionName = string.IsNullOrWhiteSpace(approverPermissionName) ? null : Check.NotNullOrWhiteSpace(approverPermissionName, nameof(approverPermissionName), 256);
        if (requiresApproval != (ApproverPermissionName != null)) throw new ArgumentException("An approver permission is required exactly when approval is required.");
        if (reviewCadenceDays is <= 0) throw new ArgumentOutOfRangeException(nameof(reviewCadenceDays));
        ReviewCadenceDays = reviewCadenceDays;
        RequiredFeatureName = string.IsNullOrWhiteSpace(requiredFeatureName) ? null : Check.NotNullOrWhiteSpace(requiredFeatureName, nameof(requiredFeatureName), 256);
        Delegable = delegable;
        EffectiveFromUtc = RequireUtc(effectiveFromUtc, nameof(effectiveFromUtc));
        EffectiveToUtc = effectiveToUtc.HasValue ? RequireUtc(effectiveToUtc.Value, nameof(effectiveToUtc)) : null;
        if (EffectiveToUtc <= EffectiveFromUtc) throw new ArgumentException("The effective interval is invalid.", nameof(effectiveToUtc));
        PublicationState = PermissionDelegationDefinitionPublicationState.Draft;
    }

    public void Publish() { if (PublicationState != PermissionDelegationDefinitionPublicationState.Draft) throw new InvalidOperationException("Only a draft descriptor version can be published."); PublicationState = PermissionDelegationDefinitionPublicationState.Published; }
    public void Retire() { if (PublicationState != PermissionDelegationDefinitionPublicationState.Published) throw new InvalidOperationException("Only a published descriptor version can be retired."); PublicationState = PermissionDelegationDefinitionPublicationState.Retired; }
    private static DateTime RequireUtc(DateTime value, string name) => value.Kind == DateTimeKind.Utc ? value : throw new ArgumentException("The timestamp must be UTC.", name);
}

internal static class PermissionDelegationDefinitionGuards
{
    public static Guid RequireUuid7(Guid id, string parameterName)
    {
        if (id == Guid.Empty || id.ToString("N", System.Globalization.CultureInfo.InvariantCulture)[12] != '7')
            throw new ArgumentException("A UUIDv7 identifier is required.", parameterName);
        return id;
    }
}
