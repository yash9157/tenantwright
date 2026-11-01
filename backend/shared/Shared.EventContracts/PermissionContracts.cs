using Shared.Domain;
using Shared.Events;

namespace Shared.EventContracts;

[EventName("Shared.EventContracts.FeatureValuesChangedTenantEto")]
[DistributedContract(1, DistributedContractScope.TenantRequired, DistributedContractDataClassification.Internal)]
public sealed class FeatureValuesChangedTenantEto : IMultiTenant
{
    public Guid? TenantId { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
    public List<string> FeatureNames { get; set; } = [];
}

[EventName("Shared.EventContracts.FeatureValuesChangedHostEto")]
[DistributedContract(1, DistributedContractScope.HostOnly, DistributedContractDataClassification.Internal)]
public sealed class FeatureValuesChangedHostEto
{
    public DateTimeOffset OccurredAtUtc { get; set; }
    public List<string> FeatureNames { get; set; } = [];
}

[EventName("Shared.EventContracts.PermissionGrantsChangedTenantEto")]
[DistributedContract(1, DistributedContractScope.TenantRequired, DistributedContractDataClassification.Internal)]
public sealed class PermissionGrantsChangedTenantEto : IMultiTenant
{
    public Guid? TenantId { get; set; }
    public string ProviderName { get; set; } = string.Empty;
    public string ProviderKey { get; set; } = string.Empty;
    public DateTimeOffset OccurredAtUtc { get; set; }
    public long Stamp { get; set; }
}

[EventName("Shared.EventContracts.PermissionGrantsChangedHostEto")]
[DistributedContract(1, DistributedContractScope.HostOnly, DistributedContractDataClassification.Internal)]
public sealed class PermissionGrantsChangedHostEto
{
    public string ProviderName { get; set; } = string.Empty;
    public string ProviderKey { get; set; } = string.Empty;
    public DateTimeOffset OccurredAtUtc { get; set; }
    public long Stamp { get; set; }
}

[EventName("Shared.EventContracts.PermissionGrantApprovalRequestedEto")]
[DistributedContract(1, DistributedContractScope.TenantRequired, DistributedContractDataClassification.Sensitive)]
public sealed class PermissionGrantApprovalRequestedEto : IMultiTenant
{
    public Guid RequestId { get; set; }
    public Guid? TenantId { get; set; }
    public string PermissionName { get; set; } = string.Empty;
    public string ProviderDisplayName { get; set; } = string.Empty;
    public string RequesterDisplayName { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public string? TicketId { get; set; }
    public List<Guid> ApproverUserIds { get; set; } = [];
    public List<string> ApproverRoleNames { get; set; } = [];
    public DateTime OccurredAtUtc { get; set; }
}

[EventName("Shared.EventContracts.PermissionGrantApprovalDecidedEto")]
[DistributedContract(1, DistributedContractScope.TenantRequired, DistributedContractDataClassification.Sensitive)]
public sealed class PermissionGrantApprovalDecidedEto : IMultiTenant
{
    public Guid RequestId { get; set; }
    public Guid? TenantId { get; set; }
    public Guid? RequesterUserId { get; set; }
    public string PermissionName { get; set; } = string.Empty;
    public string ProviderDisplayName { get; set; } = string.Empty;
    public string Decision { get; set; } = string.Empty;
    public string DecisionReason { get; set; } = string.Empty;
    public string DeciderDisplayName { get; set; } = string.Empty;
    public DateTime OccurredAtUtc { get; set; }
}

[EventName("Shared.EventContracts.TenantAdminGrantsProvisionedEto")]
[DistributedContract(1, DistributedContractScope.HostOnly, DistributedContractDataClassification.Internal)]
public sealed class TenantAdminGrantsProvisionedEto
{
    public Guid RequestId { get; set; }
    public Guid TenantId { get; set; }
    public Guid OwnerUserId { get; set; }
    public DateTime OccurredAtUtc { get; set; }
}
