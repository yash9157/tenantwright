using Shared.AuditLogs.Auditing;
using Shared.Domain;

namespace Identity.Domain.ApplicationUsers;

public class IdentityLinkUserApproval : IMultiTenant
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public Guid RequestedByUserId { get; set; }
    public Guid TargetUserId { get; set; }
    public string Status { get; set; } = IdentityLinkUserApprovalStatuses.Pending;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? RespondedAtUtc { get; set; }
    public Guid? RespondedByUserId { get; set; }
    public string? RejectionReason { get; set; }
}

public static class IdentityLinkUserApprovalStatuses
{
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
}
