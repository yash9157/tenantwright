using System.ComponentModel.DataAnnotations;
using Identity.Domain.Shared.ApplicationUsers;

namespace Identity.Application.Contracts.Mutations;

public sealed class IdentityRoleAssignmentInput
{
    public List<string> RoleNames { get; set; } = [];

    public string? ConcurrencyStamp { get; set; }

    [Required]
    [MaxLength(64)]
    public string ReasonCode { get; set; } = string.Empty;

    [MaxLength(512)]
    public string? Note { get; set; }
}

public sealed class IdentityActiveStateChangeInput
{
    public string? ConcurrencyStamp { get; set; }

    [Required]
    [MaxLength(64)]
    public string ReasonCode { get; set; } = string.Empty;

    [MaxLength(512)]
    public string? Note { get; set; }

    [MaxLength(128)]
    public string? CorrelationId { get; set; }
}

public sealed class IdentityLifecycleTransitionInput
{
    public string? ConcurrencyStamp { get; set; }

    [Required]
    [MaxLength(64)]
    public string ReasonCode { get; set; } = string.Empty;

    [MaxLength(512)]
    public string? Note { get; set; }

    [MaxLength(128)]
    public string? CorrelationId { get; set; }
}

public sealed class IdentityLockStateChangeInput
{
    public string? ConcurrencyStamp { get; set; }

    [Required]
    [MaxLength(64)]
    public string ReasonCode { get; set; } = string.Empty;

    [MaxLength(512)]
    public string? Note { get; set; }

    [MaxLength(128)]
    public string? CorrelationId { get; set; }
}

/// <summary>
/// Host-governed designation of the tenant's primary administrator. The target must already
/// be an active tenant administrator; designation never grants, removes, or changes roles.
/// </summary>
public sealed class PrimaryAdministratorHandoverInput
{
    [Required]
    public Guid TargetUserId { get; set; }

    [Required]
    [MaxLength(128)]
    public string TargetConcurrencyStamp { get; set; } = string.Empty;

    /// <summary>
    /// The primary administrator the operator saw before submitting. Null is valid only when
    /// the tenant currently has no designated administrator.
    /// </summary>
    public Guid? ExpectedCurrentPrimaryAdministratorUserId { get; set; }

    [Required]
    [MaxLength(64)]
    public string ReasonCode { get; set; } = string.Empty;

    [MaxLength(512)]
    public string? Note { get; set; }

    [MaxLength(128)]
    public string? CorrelationId { get; set; }
}

public sealed class GetIdentityLifecycleHistoryInput
{
    [Range(0, int.MaxValue)]
    public int SkipCount { get; set; }

    [Range(1, 100)]
    public int MaxResultCount { get; set; } = 50;
}

public sealed class IdentityUserLifecycleHistoryDto
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public Guid UserId { get; set; }
    public IdentityUserLifecycleStatus? PreviousStatus { get; set; }
    public IdentityUserLifecycleStatus CurrentStatus { get; set; }
    public DateTime EffectiveAtUtc { get; set; }
    public DateTime ChangedAtUtc { get; set; }
    public string ReasonCode { get; set; } = string.Empty;
    public string? Note { get; set; }
    public Guid? ActorUserId { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public bool IsMigrationBackfill { get; set; }
}
