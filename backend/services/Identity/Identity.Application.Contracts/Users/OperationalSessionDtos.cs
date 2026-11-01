using System.ComponentModel.DataAnnotations;
using Identity.Domain.Shared.ApplicationUsers;
using Shared;

namespace Identity.Application.Contracts.Users;

public sealed class OperationalSessionDto
{
    public Guid Id { get; set; }
    public string SessionId { get; set; } = default!;
    public Guid? TenantId { get; set; }
    public string? TenantName { get; set; }
    public Guid UserId { get; set; }
    public string? UserName { get; set; }
    public string? Email { get; set; }
    public string? DeviceId { get; set; }
    public string? ClientId { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public DateTime CreationTime { get; set; }
    public DateTime LastActiveTime { get; set; }
    public string Status { get; set; } = IdentitySessionStatus.Active.ToString();
    public DateTime? RevokedAtUtc { get; set; }
    public Guid? RevokedByUserId { get; set; }
    public string? RevocationSource { get; set; }
    public string? RevocationReason { get; set; }
}

/// <remarks>
/// The sortable set mirrors <c>IdentitySessionAppService.ApplyOperationalSorting</c>. It is wider than
/// the per-user session sort because this cross-tenant operational view also orders by tenant and by
/// the owning user.
/// </remarks>
[SortableFields(
    "CreationTime",
    "LastActiveTime",
    "Status",
    "TenantName",
    "UserName",
    "Email",
    "RevokedAtUtc",
    DefaultSort = "LastActiveTime desc")]
public sealed class GetOperationalSessionsInput : PagedRequestDto
{
    [MaxLength(32)]
    public string? Status { get; set; }

    public Guid? TenantId { get; set; }

    [MaxLength(256)]
    public string? TenantName { get; set; }

    public Guid? UserId { get; set; }

    [MaxLength(256)]
    public string? UserFilter { get; set; }

    [MaxLength(64)]
    public string? ClientId { get; set; }

    [MaxLength(256)]
    public string? DeviceFilter { get; set; }

    [MaxLength(64)]
    public string? IpFilter { get; set; }

    public DateTime? CreatedFromUtc { get; set; }
    public DateTime? CreatedToUtc { get; set; }
    public DateTime? LastActiveFromUtc { get; set; }
    public DateTime? LastActiveToUtc { get; set; }
    public DateTime? RevokedFromUtc { get; set; }
    public DateTime? RevokedToUtc { get; set; }
}

public sealed class BulkRevokeSessionsInput
{
    [Required]
    [MinLength(1)]
    public List<Guid> SessionIds { get; set; } = [];

    [MaxLength(512)]
    public string? Reason { get; set; }
}

public sealed class ManagedSessionRevokeInput
{
    [Required]
    [MaxLength(512)]
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// Optional reason supplied when an administrator revokes every session for a user or tenant.
/// The endpoint continues to accept an empty body for existing callers.
/// </summary>
public sealed class ManagedBulkSessionRevokeInput
{
    [Required]
    [MaxLength(512)]
    public string Reason { get; set; } = string.Empty;
}

public sealed class BulkRevokeSessionsResultDto
{
    public int RequestedCount { get; set; }
    public int RevokedCount { get; set; }
    public int AlreadyRevokedCount { get; set; }
}
