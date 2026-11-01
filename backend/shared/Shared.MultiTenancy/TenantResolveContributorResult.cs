namespace Shared.MultiTenancy;

public sealed class TenantResolveContributorResult
{
    public TenantResolveStatus Status { get; }

    public Guid? TenantId { get; }

    public string? FailureReason { get; }

    public TenantResolveContributorResult(TenantResolveStatus status, Guid? tenantId, string? failureReason)
    {
        Status = status;
        TenantId = tenantId;
        FailureReason = failureReason;
    }
}
