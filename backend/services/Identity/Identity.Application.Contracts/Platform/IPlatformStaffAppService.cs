using Identity.Application.Contracts.Users;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using Identity.Application.Contracts.Mutations;
using Identity.Domain.Shared.ApplicationUsers;
using Shared;

namespace Identity.Application.Contracts.Platform;

public class PlatformStaffDto
{
    public string Id { get; set; } = default!;
    public string UserName { get; set; } = default!;
    public string Email { get; set; } = default!;
    public string? Name { get; set; }
    public string? Surname { get; set; }
    public string? PhoneNumber { get; set; }
    public bool IsActive { get; set; }
    public IdentityUserLifecycleStatus LifecycleStatus { get; set; }
    public Guid? TenantId { get; set; }
    public bool IsLocked { get; set; }
    public DateTimeOffset? LockedUntilUtc { get; set; }
    public List<string> Roles { get; set; } = [];

    /// <summary>Whether the primary email address is confirmed.</summary>
    public bool EmailConfirmed { get; set; }

    /// <summary>Whether two-factor authentication is enabled for the staff user.</summary>
    public bool TwoFactorEnabled { get; set; }

    /// <summary>Whether an email change is pending confirmation (raw pending value is detail-only).</summary>
    public bool HasPendingEmail { get; set; }

    /// <summary>Optimistic-concurrency token; echo back on update to enforce a stale-write check.</summary>
    public string? ConcurrencyStamp { get; set; }

    /// <summary>Most recent successful login (derived from security logs). Null if never/unknown.</summary>
    public DateTime? LastLoginAtUtc { get; set; }

    /// <summary>Most recent session activity (derived from sessions). Null if none.</summary>
    public DateTime? LastActiveAtUtc { get; set; }

    /// <summary>When the staff record was created. Null for legacy records whose time was never captured.</summary>
    public DateTime? CreatedAtUtc { get; set; }
}

/// <summary>
/// Detail projection for a single platform staff user (GET by id). Superset of
/// <see cref="PlatformStaffDto"/> with admin-only fields not appropriate for a list row.
/// </summary>
public sealed class PlatformStaffDetailDto : PlatformStaffDto
{
    public bool PhoneNumberConfirmed { get; set; }
    public int AccessFailedCount { get; set; }
    public bool ShouldChangePasswordOnNextLogin { get; set; }
    public bool RequireEmailActivationBeforeLogin { get; set; }
    public string? PendingEmail { get; set; }
    public DateTime? PendingEmailRequestedAtUtc { get; set; }
    public int ActiveSessionCount { get; set; }

    /// <summary>Zero-filled daily successful-sign-in counts over the recent window (C9).</summary>
    public List<Identity.Application.Contracts.Users.UserSignInActivityPointDto> SignInActivity { get; set; } = [];
}

public sealed class PlatformStaffCreateDto
{
    public string UserName { get; set; } = default!;
    public string Email { get; set; } = default!;
    public string Password { get; set; } = default!;

    [MaxLength(128)]
    public string? Name { get; set; }

    [MaxLength(128)]
    public string? Surname { get; set; }

    public string? PhoneNumber { get; set; }
    public bool IsActive { get; set; } = true;
    public List<string> RoleNames { get; set; } = [];
}

public sealed class PlatformStaffUpdateDto
{
    public string Email { get; set; } = default!;

    [MaxLength(128)]
    public string? Name { get; set; }

    [MaxLength(128)]
    public string? Surname { get; set; }

    public string? PhoneNumber { get; set; }
    public bool IsActive { get; set; }
    public List<string> RoleNames { get; set; } = [];

    /// <summary>
    /// Optional optimistic-concurrency token. When supplied, the update is rejected if it does not
    /// match the current stored value (stale write). Omit to keep the prior last-write-wins behavior.
    /// </summary>
    public string? ConcurrencyStamp { get; set; }
}

/// <remarks>
/// The sortable set mirrors <c>PlatformStaffAppService.ApplySorting</c>. <c>LastLogin</c> is included
/// deliberately even though it aggregates over security logs rather than reading a column — the
/// attribute declares the contract, the application service owns the query.
/// </remarks>
[SortableFields(
    "UserName",
    "Email",
    "IsActive",
    "Created",
    "LastLogin",
    DefaultSort = "UserName")]
public sealed class GetPlatformStaffInput : PagedRequestDto
{
    [MaxLength(256)]
    public string? Filter { get; set; }

    public bool? IsActive { get; set; }
    public IdentityUserLifecycleStatus? LifecycleStatus { get; set; }

    [MaxLength(64)]
    public string? RoleName { get; set; }

    public bool? EmailConfirmed { get; set; }

    public bool? TwoFactorEnabled { get; set; }

    public bool? IsLocked { get; set; }
    public DateTime? CreatedFromUtc { get; set; }
    public DateTime? CreatedToUtc { get; set; }
    public DateTime? LastLoginFromUtc { get; set; }
    public DateTime? LastLoginToUtc { get; set; }
}

public interface IPlatformStaffAppService
{
    Task<PlatformStaffDetailDto> GetAsync(string id);
    Task<PagedResultDto<PlatformStaffDto>> GetPagedListAsync(GetPlatformStaffInput input);
    Task<PlatformStaffDto> CreateAsync(PlatformStaffCreateDto input);
    Task<PlatformStaffDto> UpdateAsync(string id, PlatformStaffUpdateDto input);
    Task<PlatformStaffDto> SetRolesAsync(string id, IdentityRoleAssignmentInput input);
    Task<PlatformStaffDto> ActivateAsync(string id, IdentityActiveStateChangeInput input);
    Task<PlatformStaffDto> DeactivateAsync(string id, IdentityActiveStateChangeInput input);
    Task<PlatformStaffDto> SuspendAsync(string id, IdentityLifecycleTransitionInput input);
    Task<PlatformStaffDto> RestoreAsync(string id, IdentityLifecycleTransitionInput input);
    Task<PlatformStaffDto> TerminateAsync(string id, IdentityLifecycleTransitionInput input);
    Task<PlatformStaffDto> RehireAsync(string id, IdentityLifecycleTransitionInput input);
    Task<PlatformStaffDto> ArchiveAsync(string id, IdentityLifecycleTransitionInput input);
    Task<PagedResultDto<IdentityUserLifecycleHistoryDto>> GetLifecycleHistoryAsync(string id, GetIdentityLifecycleHistoryInput input);
    Task LockAsync(string id);
    Task UnlockAsync(string id);
    Task DeleteAsync(string id);
}
