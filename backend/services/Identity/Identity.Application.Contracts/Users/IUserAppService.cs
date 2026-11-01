using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using Identity.Application.Contracts.Mutations;
using Identity.Domain.Shared.ApplicationUsers;
using Shared;

namespace Identity.Application.Contracts.Users;

public class UserDto
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
    public string? TenantName { get; set; }
    public bool IsLocked { get; set; }
    public DateTimeOffset? LockedUntilUtc { get; set; }
    public List<string> Roles { get; set; } = new();

    /// <summary>Whether the primary email address is confirmed.</summary>
    public bool EmailConfirmed { get; set; }

    /// <summary>Whether two-factor authentication is enabled for the user.</summary>
    public bool TwoFactorEnabled { get; set; }

    /// <summary>Whether an email change is pending confirmation (raw pending value is detail-only).</summary>
    public bool HasPendingEmail { get; set; }

    /// <summary>Optimistic-concurrency token; echo back on update to enforce a stale-write check.</summary>
    public string? ConcurrencyStamp { get; set; }

    /// <summary>Most recent successful login (derived from security logs). Null if never/unknown.</summary>
    public DateTime? LastLoginAtUtc { get; set; }

    /// <summary>Most recent session activity (derived from sessions). Null if none.</summary>
    public DateTime? LastActiveAtUtc { get; set; }

    /// <summary>When the user record was created. Null for legacy records whose time was never captured.</summary>
    public DateTime? CreatedAtUtc { get; set; }
}

/// <summary>
/// Detail projection for a single user (GET by id). Superset of <see cref="UserDto"/> adding
/// admin-only fields that are not appropriate for a list row (raw pending email, session count,
/// verification/lifecycle flags).
/// </summary>
public class UserDetailDto : UserDto
{
    public bool PhoneNumberConfirmed { get; set; }
    public int AccessFailedCount { get; set; }
    public bool ShouldChangePasswordOnNextLogin { get; set; }
    public bool RequireEmailActivationBeforeLogin { get; set; }

    /// <summary>Pending (unconfirmed) email address, if an email change is in progress. Detail-only (PII).</summary>
    public string? PendingEmail { get; set; }
    public DateTime? PendingEmailRequestedAtUtc { get; set; }

    /// <summary>Count of currently active sessions for the user.</summary>
    public int ActiveSessionCount { get; set; }

    /// <summary>
    /// Zero-filled daily successful-sign-in counts over the recent window (C9). Powers
    /// the "active vs dormant" sparkline on the person detail; magnitude is secondary to
    /// shape.
    /// </summary>
    public List<UserSignInActivityPointDto> SignInActivity { get; set; } = [];
}

/// <summary>One day of successful sign-in activity for a user (C9 sparkline).</summary>
public sealed class UserSignInActivityPointDto
{
    public DateTime DateUtc { get; set; }
    public int Count { get; set; }
}

public class UserCreateDto
{
    public Guid? TenantId { get; set; }
    public string UserName { get; set; } = default!;
    public string Email { get; set; } = default!;
    public string Password { get; set; } = default!;

    [MaxLength(128)]
    public string? Name { get; set; }

    [MaxLength(128)]
    public string? Surname { get; set; }

    public string? PhoneNumber { get; set; }
    public bool IsActive { get; set; } = true;
    public List<string> RoleNames { get; set; } = new();
}

public class UserUpdateDto
{
    public string Email { get; set; } = default!;

    [MaxLength(128)]
    public string? Name { get; set; }

    [MaxLength(128)]
    public string? Surname { get; set; }

    public string? PhoneNumber { get; set; }
    public bool IsActive { get; set; }
    public List<string> RoleNames { get; set; } = new();

    /// <summary>
    /// Optional optimistic-concurrency token. When supplied, the update is rejected if it does not
    /// match the current stored value (stale write). Omit to keep the prior last-write-wins behavior.
    /// </summary>
    public string? ConcurrencyStamp { get; set; }
}

/// <remarks>
/// The sortable set mirrors <c>UserAppService.ApplySorting</c>. <c>LastLogin</c> aggregates over the
/// security log rather than reading a column — the attribute declares the contract, the application
/// service owns the query.
/// </remarks>
[SortableFields(
    "UserName",
    "Email",
    "IsActive",
    "Created",
    "LastLogin",
    DefaultSort = "UserName")]
public class GetUsersInput : PagedRequestDto
{
    public Guid? TenantId { get; set; }

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

/// <summary>
/// Host-only tenant-user directory input. A missing tenant id deliberately means
/// every tenant user visible to the host operator; the tenant-admin endpoint does
/// not share that meaning.
/// </summary>
public sealed class GetHostTenantUsersInput : GetUsersInput
{
}

/// <summary>
/// Batch request for the primary administrator of each of a set of tenants
/// (host cross-tenant read). Replaces the per-tenant N+1 the console did when
/// building the tenant-admins summary (design §8, Option A). The role defaults
/// to the tenant-admin role when not supplied.
/// </summary>
public class TenantPrimaryAdminsInput
{
    public List<Guid> TenantIds { get; set; } = new();

    [MaxLength(64)]
    public string? RoleName { get; set; }
}

/// <summary>
/// Primary-admin projection for a single tenant, keyed by tenant id. Carries no
/// tenant name/active state — the console already has those from the tenant list
/// and merges this admin projection onto them client-side.
/// </summary>
public class TenantPrimaryAdminDto
{
    public Guid TenantId { get; set; }
    public string? PrimaryAdminUserId { get; set; }
    public string? PrimaryAdminName { get; set; }
    public string? PrimaryAdminEmail { get; set; }
    public bool IsActive { get; set; }
}

public interface IUserAppService
{
    Task<UserDetailDto> GetAsync(string id);
    Task<PagedResultDto<UserDto>> GetPagedListAsync(GetUsersInput input);
    Task<PagedResultDto<UserDto>> GetHostPagedListAsync(GetHostTenantUsersInput input);
    Task<List<TenantPrimaryAdminDto>> GetPrimaryAdminsAsync(TenantPrimaryAdminsInput input);
    Task<TenantPrimaryAdminDto> HandoverPrimaryAdministratorAsync(PrimaryAdministratorHandoverInput input);
    Task<BulkUserActionResultDto> ActivateBulkAsync(BulkUserActionInput input);
    Task<BulkUserActionResultDto> DeactivateBulkAsync(BulkUserActionInput input);
    Task<BulkUserActionResultDto> RevokeSessionsBulkAsync(BulkUserActionInput input);
    Task<UserExportJobDto> CreateExportAsync(RequestUserExportInput input);
    Task<UserExportJobDto> GetExportAsync(long id);
    Task<UserExportDownloadDto> GetExportDownloadAsync(long id, string token);
    Task<UserImportValidationDto> ValidateImportAsync(ValidateUserImportInput input);
    Task<UserImportJobDto> StartImportAsync(StartUserImportInput input);
    Task<UserImportJobDto> GetImportAsync(long id);
    Task<UserDto> CreateAsync(UserCreateDto input);
    Task<UserDto> UpdateAsync(string id, UserUpdateDto input);
    Task<UserDto> SetRolesAsync(string id, IdentityRoleAssignmentInput input);
    Task<UserDto> ActivateAsync(string id, IdentityActiveStateChangeInput input);
    Task<UserDto> DeactivateAsync(string id, IdentityActiveStateChangeInput input);
    Task<UserDto> SuspendAsync(string id, IdentityLifecycleTransitionInput input);
    Task<UserDto> RestoreAsync(string id, IdentityLifecycleTransitionInput input);
    Task<UserDto> TerminateAsync(string id, IdentityLifecycleTransitionInput input);
    Task<UserDto> RehireAsync(string id, IdentityLifecycleTransitionInput input);
    Task<UserDto> ArchiveAsync(string id, IdentityLifecycleTransitionInput input);
    Task<PagedResultDto<IdentityUserLifecycleHistoryDto>> GetLifecycleHistoryAsync(string id, GetIdentityLifecycleHistoryInput input);
    Task LockAsync(string id);
    Task UnlockAsync(string id);
    Task<UserDto> LockAsync(string id, IdentityLockStateChangeInput input);
    Task<UserDto> UnlockAsync(string id, IdentityLockStateChangeInput input);
    Task DeleteAsync(string id);
}

/// <summary>
/// Explicit host-context facade for tenant-owned user operations. The tenant id
/// is part of every detail or mutation request so a host operator cannot act on
/// a user without naming the tenant scope being managed.
/// </summary>
public interface IHostTenantUserAppService
{
    Task<UserDetailDto> GetAsync(Guid tenantId, string id);
    Task<PagedResultDto<UserDto>> GetPagedListAsync(GetHostTenantUsersInput input);
    Task<UserDto> UpdateAsync(Guid tenantId, string id, UserUpdateDto input);
    Task<UserDto> SetRolesAsync(Guid tenantId, string id, IdentityRoleAssignmentInput input);
    Task<UserDto> SuspendAsync(Guid tenantId, string id, IdentityLifecycleTransitionInput input);
    Task<UserDto> RestoreAsync(Guid tenantId, string id, IdentityLifecycleTransitionInput input);
    Task<UserDto> TerminateAsync(Guid tenantId, string id, IdentityLifecycleTransitionInput input);
    Task<UserDto> RehireAsync(Guid tenantId, string id, IdentityLifecycleTransitionInput input);
    Task<UserDto> ArchiveAsync(Guid tenantId, string id, IdentityLifecycleTransitionInput input);
    Task<PagedResultDto<IdentityUserLifecycleHistoryDto>> GetLifecycleHistoryAsync(
        Guid tenantId,
        string id,
        GetIdentityLifecycleHistoryInput input);
    Task<UserDto> LockAsync(Guid tenantId, string id, IdentityLockStateChangeInput input);
    Task<UserDto> UnlockAsync(Guid tenantId, string id, IdentityLockStateChangeInput input);
    Task<TenantPrimaryAdminDto> HandoverPrimaryAdministratorAsync(Guid tenantId, PrimaryAdministratorHandoverInput input);
}
