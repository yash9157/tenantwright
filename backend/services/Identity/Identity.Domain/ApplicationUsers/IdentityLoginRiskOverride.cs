using Identity.Domain.Shared.ApplicationUsers;
using Shared.AuditLogs.Auditing;
using Shared.Domain;

namespace Identity.Domain.ApplicationUsers;

public class IdentityLoginRiskOverride : IMultiTenant
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public Guid UserId { get; set; }
    public IdentityLoginRiskOverrideMode ModeOverride { get; set; } = IdentityLoginRiskOverrideMode.Allow;
    public string? Reason { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTime CreationTime { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
    public bool IsActive { get; set; } = true;
}
