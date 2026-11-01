using Shared.Domain;
using Shared.Events;

namespace Shared.EventContracts;

[EventName("Shared.EventContracts.IdentitySessionRevokedTenantEto")]
[DistributedContract(1, DistributedContractScope.TenantRequired, DistributedContractDataClassification.Sensitive)]
public sealed class IdentitySessionRevokedTenantEto : IMultiTenant
{
    public Guid? TenantId { get; set; }
    public Guid UserId { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public DateTime OccurredAtUtc { get; set; }
}

[EventName("Shared.EventContracts.IdentitySessionRevokedHostEto")]
[DistributedContract(1, DistributedContractScope.HostOnly, DistributedContractDataClassification.Sensitive)]
public sealed class IdentitySessionRevokedHostEto
{
    public Guid UserId { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public DateTime OccurredAtUtc { get; set; }
}

[EventName("Shared.EventContracts.IdentityPlatformRoleRetirementRequestedEto")]
[DistributedContract(1, DistributedContractScope.HostOnly, DistributedContractDataClassification.Internal)]
public sealed class IdentityPlatformRoleRetirementRequestedEto
{
    public Guid RequestId { get; set; }
    public Guid RoleId { get; set; }
    public long LifecycleVersion { get; set; }
    public DateTime OccurredAtUtc { get; set; }
}

[EventName("Shared.EventContracts.PermissionsPlatformRoleRetirementCompletedEto")]
[DistributedContract(1, DistributedContractScope.HostOnly, DistributedContractDataClassification.Internal)]
public sealed class PermissionsPlatformRoleRetirementCompletedEto
{
    public Guid RequestId { get; set; }
    public Guid RoleId { get; set; }
    public long LifecycleVersion { get; set; }
    public DateTime OccurredAtUtc { get; set; }
}
