using Shared.Domain;

namespace Identity.Domain.ApplicationUsers;

public class IdentityPublicInvitation : IMultiTenant
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    Guid? IMultiTenant.TenantId => TenantId;
    public string TenantName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    public string? RoleName { get; set; }
    public string? SuggestedUserName { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? AcceptedAtUtc { get; set; }
    public Guid? AcceptedUserId { get; set; }
    public DateTime? CanceledAtUtc { get; set; }
    public Guid? CanceledByUserId { get; set; }
    public string? CancelReason { get; set; }
    public Guid? OwnershipRequestId { get; set; }
}
