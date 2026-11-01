using System;
using Shared.AuditLogs.Auditing;
using Shared.Domain;
using Identity.Domain.Shared.ApplicationUsers;

namespace Identity.Domain.ApplicationUsers;

public class IdentitySession : IMultiTenant
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public Guid UserId { get; set; }

    public string? DeviceId { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public DateTime CreationTime { get; set; }
    public DateTime LastActiveTime { get; set; }
    public IdentitySessionStatus Status { get; set; } = IdentitySessionStatus.Active;
    public DateTime? RevokedAtUtc { get; set; }
    public Guid? RevokedByUserId { get; set; }
    public string? RevocationSource { get; set; }
    public string? RevocationReason { get; set; }

    // Legacy compatibility fields kept for existing controllers/clients.
    public string SessionId { get; set; } = default!;
    public string? ClientId { get; set; }
    public string? Location { get; set; }
}
