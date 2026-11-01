using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using Identity.Domain.Shared.ApplicationUsers;
using Shared;

namespace Identity.Application.Contracts.Users;

public class RevokeSessionInput
{
    [Required]
    public Guid UserId { get; set; }

    [Required]
    public string SessionId { get; set; } = default!;

    [Required]
    public Guid? TenantId { get; set; }
}

public class ValidateSessionInput
{
    [Required]
    public Guid UserId { get; set; }

    [Required]
    public string SessionId { get; set; } = default!;

    public Guid? TenantId { get; set; }
}

public class RevokeOwnSessionInput
{
    [Required]
    public string SessionId { get; set; } = default!;
}

public class IdentitySessionDto
{
    public Guid Id { get; set; }
    public string SessionId { get; set; } = default!;
    public string? DeviceId { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public string DeviceLabel { get; set; } = "Unrecognized device";
    public bool IsCurrentSession { get; set; }
    public Guid UserId { get; set; }
    public DateTime CreationTime { get; set; }
    public DateTime LastActiveTime { get; set; }
    public string Status { get; set; } = IdentitySessionStatus.Active.ToString();
    public DateTime? RevokedAtUtc { get; set; }
    public Guid? RevokedByUserId { get; set; }
    public string? RevocationSource { get; set; }
    public string? RevocationReason { get; set; }

}

/// <remarks>
/// The sortable set mirrors <c>IdentitySessionAppService.ApplySorting</c>. The default keeps the most
/// recently active session first, which is the order an administrator reviewing a user's sessions
/// expects.
/// </remarks>
[SortableFields(
    "CreationTime",
    "LastActiveTime",
    "Status",
    "RevokedAtUtc",
    "IpAddress",
    DefaultSort = "LastActiveTime desc")]
public class GetUserSessionsPagedInput : PagedRequestDto
{
    [MaxLength(256)]
    public string? Filter { get; set; }

    [MaxLength(256)]
    public string? DeviceFilter { get; set; }

    [MaxLength(32)]
    public string? Status { get; set; }

    public DateTime? FromUtc { get; set; }

    public DateTime? ToUtc { get; set; }

    public DateTime? RevokedFromUtc { get; set; }

    public DateTime? RevokedToUtc { get; set; }
}

public interface IIdentitySessionAppService
{
    Task<List<IdentitySessionDto>> GetListAsync();
    Task RevokeAsync(Guid id);
    Task RevokeCurrentBySessionIdAsync(string sessionId);
    Task RevokeBySessionIdAsync(Guid userId, string sessionId, Guid? tenantId = null);
    Task<int> RevokeAllByUserIdAsync(Guid userId, string? revocationReason = null);
    Task<int> RevokeOthersAsync();
    Task<List<IdentitySessionDto>> GetListByUserIdAsync(Guid userId, Guid? tenantId = null);
    Task<PagedResultDto<IdentitySessionDto>> GetPagedListByUserIdAsync(Guid userId, GetUserSessionsPagedInput input);
    Task<PagedResultDto<OperationalSessionDto>> GetManagementPagedListAsync(GetOperationalSessionsInput input);
    Task<BulkRevokeSessionsResultDto> RevokeManagedAsync(Guid sessionId, ManagedSessionRevokeInput input);
    Task<BulkRevokeSessionsResultDto> RevokeSelectedAsync(BulkRevokeSessionsInput input);
    Task<int> RevokeAllByTenantIdAsync(Guid tenantId, string? revocationReason = null);
    Task<int> CountActiveByTenantIdAsync(Guid tenantId, CancellationToken cancellationToken = default);
    Task<bool> ValidateAsync(Guid userId, string sessionId, Guid? tenantId = null);
}
