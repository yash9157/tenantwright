using Shared.Events;

namespace Shared.EventContracts;

public enum TenantLifecycleStatusEto
{
    Active = 0,
    Provisioning = 1,
    Suspended = 2,
    ProvisioningFailed = 3,
    Decommissioned = 4
}

public enum TenantOwnershipRequestKindEto
{
    InitialOwner = 0,
    Transfer = 1,
    Recovery = 2
}

[EventName("Shared.EventContracts.TenantInitialOwnerOnboardingRequestedEto")]
[DistributedContract(1, DistributedContractScope.HostOnly, DistributedContractDataClassification.Internal)]
public sealed class TenantInitialOwnerOnboardingRequestedEto
{
    public Guid RequestId { get; set; }
    public Guid TenantId { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime OccurredAtUtc { get; set; }
}

[EventName("Shared.EventContracts.TenantInitialOwnerOnboardingCompletedEto")]
[DistributedContract(1, DistributedContractScope.HostOnly, DistributedContractDataClassification.Internal)]
public sealed class TenantInitialOwnerOnboardingCompletedEto
{
    public Guid RequestId { get; set; }
    public Guid TenantId { get; set; }
    public DateTime OccurredAtUtc { get; set; }
}

[EventName("Shared.EventContracts.TenantOwnershipCancellationRequestedEto")]
[DistributedContract(1, DistributedContractScope.HostOnly, DistributedContractDataClassification.Internal)]
public sealed class TenantOwnershipCancellationRequestedEto
{
    public Guid RequestId { get; set; }
    public Guid TenantId { get; set; }
    public string ReasonCode { get; set; } = string.Empty;
    public DateTime OccurredAtUtc { get; set; }
}

[EventName("Shared.EventContracts.TenantOwnershipInvitationRequestedEto")]
[DistributedContract(1, DistributedContractScope.HostOnly, DistributedContractDataClassification.Sensitive)]
public sealed class TenantOwnershipInvitationRequestedEto
{
    public Guid RequestId { get; set; }
    public Guid TenantId { get; set; }
    public string TenantName { get; set; } = string.Empty;
    public string TargetEmail { get; set; } = string.Empty;
    public string? SuggestedUserName { get; set; }
    public TenantOwnershipRequestKindEto Kind { get; set; }
    public bool IsResend { get; set; }
    public DateTime OccurredAtUtc { get; set; }
}

[EventName("Shared.EventContracts.TenantOwnershipInvitationCanceledEto")]
[DistributedContract(1, DistributedContractScope.HostOnly, DistributedContractDataClassification.Internal)]
public sealed class TenantOwnershipInvitationCanceledEto
{
    public Guid RequestId { get; set; }
    public Guid TenantId { get; set; }
    public DateTime OccurredAtUtc { get; set; }
}

[EventName("Shared.EventContracts.TenantOwnershipInvitationCreatedEto")]
[DistributedContract(1, DistributedContractScope.HostOnly, DistributedContractDataClassification.Internal)]
public sealed class TenantOwnershipInvitationCreatedEto
{
    public Guid RequestId { get; set; }
    public Guid TenantId { get; set; }
    public Guid InvitationId { get; set; }
    public DateTime OccurredAtUtc { get; set; }
}

[EventName("Shared.EventContracts.TenantOwnershipInvitationVerifiedEto")]
[DistributedContract(1, DistributedContractScope.HostOnly, DistributedContractDataClassification.Sensitive)]
public sealed class TenantOwnershipInvitationVerifiedEto
{
    public Guid RequestId { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public string? UserName { get; set; }
    public string? DisplayName { get; set; }
    public string? Email { get; set; }
}

[EventName("Shared.EventContracts.TenantOwnershipCompletedEto")]
[DistributedContract(1, DistributedContractScope.HostOnly, DistributedContractDataClassification.Internal)]
public sealed class TenantOwnershipCompletedEto
{
    public Guid RequestId { get; set; }
    public Guid TenantId { get; set; }
    public Guid NewOwnerUserId { get; set; }
    public Guid? PreviousOwnerUserId { get; set; }
    public DateTime OccurredAtUtc { get; set; }
}

[EventName("Shared.EventContracts.TenantDeactivatedEto")]
[DistributedContract(1, DistributedContractScope.HostOnly, DistributedContractDataClassification.Sensitive)]
public sealed class TenantDeactivatedEto
{
    public Guid TenantId { get; set; }
    public string? TenantName { get; set; }
    public DateTime OccurredAtUtc { get; set; }
}

[EventName("Shared.EventContracts.TenantClosureChangedEto")]
[DistributedContract(1, DistributedContractScope.HostOnly, DistributedContractDataClassification.Internal)]
public sealed class TenantClosureChangedEto
{
    public Guid RequestId { get; set; }
    public Guid TenantId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public bool IsClosed { get; set; }
    public string ReasonCode { get; set; } = string.Empty;
    public DateTime OccurredAtUtc { get; set; }
}

/// <summary>
/// The Tenant service committed a change to one tenant's dedicated database assignment for one service database
/// name. Published through the Tenant outbox in the same transaction as the change. Each service that owns
/// <see cref="DatabaseName"/> migrates the dedicated database and re-runs its tenant seeders; services that do not own
/// the name ignore it. No data is moved between databases by the assignment.
/// </summary>
[EventName("Shared.EventContracts.TenantDatabaseChangedEto")]
[DistributedContract(1, DistributedContractScope.HostOnly, DistributedContractDataClassification.Internal)]
public sealed class TenantDatabaseChangedEto
{
    public Guid TenantId { get; set; }

    /// <summary>The service database name from the reviewed database catalog, for example <c>Notifications</c>.</summary>
    public string DatabaseName { get; set; } = string.Empty;

    public TenantDatabaseChangeKindEto ChangeKind { get; set; }

    /// <summary>
    /// Non-reversible fingerprint of the committed target (host, port, database); null when the assignment was
    /// removed. Consumers compare it with what they resolve to detect a tenant cache that has not converged yet.
    /// </summary>
    public string? TargetFingerprint { get; set; }

    public DateTime OccurredAtUtc { get; set; }
}

public enum TenantDatabaseChangeKindEto
{
    /// <summary>The tenant moved from the shared database to a dedicated one.</summary>
    Assigned = 1,

    /// <summary>The dedicated database was re-pointed to a different target.</summary>
    Reassigned = 2,

    /// <summary>The same target was re-saved (for example rotated credentials).</summary>
    Updated = 3,

    /// <summary>The tenant returned to the shared database.</summary>
    Removed = 4
}

[EventName("Shared.EventContracts.TenantLifecycleChangedEto")]
[DistributedContract(1, DistributedContractScope.HostOnly, DistributedContractDataClassification.Sensitive)]
public sealed class TenantLifecycleChangedEto
{
    public Guid TenantId { get; set; }
    public string? TenantName { get; set; }
    public long LifecycleRevision { get; set; }
    public TenantLifecycleStatusEto PreviousStatus { get; set; }
    public TenantLifecycleStatusEto CurrentStatus { get; set; }
    public string ReasonCode { get; set; } = string.Empty;
    public DateTime OccurredAtUtc { get; set; }
}

[EventName("Shared.EventContracts.TenantOrganizationProfileChangedEto")]
[DistributedContract(1, DistributedContractScope.HostOnly, DistributedContractDataClassification.Internal)]
public sealed class TenantOrganizationProfileChangedEto
{
    public Guid TenantId { get; set; }
    public IReadOnlyList<string> ChangedFields { get; set; } = [];
    public DateTime OccurredAtUtc { get; set; }
}
