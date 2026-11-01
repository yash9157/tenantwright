using Permissions.Domain.Shared.Enums;
using Permissions.Application.Contracts.PermissionManagement;

using System.ComponentModel.DataAnnotations;

namespace Permissions.Application.Contracts.TenantPermissionAccess;

public enum TenantDelegationTargetKind
{
    User = 1,
    Role = 2
}

/// <remarks>
/// The five <see cref="RequiredAttribute"/> members were already enforced at runtime — a missing target,
/// permission or idempotency key is rejected by the application service, and an absent
/// <see cref="ExpectedProviderVersion"/> fails the optimistic-concurrency comparison and returns a
/// conflict. They carried no annotation, so nothing marked them required in the published OpenAPI
/// document and a generated client typed all five as optional. Declaring them states the real contract:
/// a caller is refused at the boundary with a field-level error instead of learning from a 400 message
/// or a confusing 409.
/// </remarks>
public sealed class TenantDelegationMutationInput
{
    [Required]
    public TenantDelegationTargetKind TargetKind { get; set; }

    [Required]
    public Guid TargetId { get; set; }

    [Required]
    [MaxLength(256)]
    public string PermissionName { get; set; } = string.Empty;

    public bool IsGranted { get; set; } = true;
    public PermissionGrantEffect Effect { get; set; } = PermissionGrantEffect.Allow;
    public DateTime? ExpiresAtUtc { get; set; }
    public string? Reason { get; set; }
    public string? TicketId { get; set; }

    /// <summary>
    /// Optimistic-concurrency token from the caller's last read. Omitting it does not skip the check —
    /// the comparison fails and the mutation is refused with a conflict.
    /// </summary>
    [Required]
    public string ExpectedProviderVersion { get; set; } = string.Empty;

    [Required]
    public string IdempotencyKey { get; set; } = string.Empty;
}

public sealed class TenantDelegationMutationResult
{
    public Guid TargetId { get; set; }
    public TenantDelegationTargetKind TargetKind { get; set; }
    public string PermissionName { get; set; } = string.Empty;
    public bool IsGranted { get; set; }
    public PermissionGrantEffect Effect { get; set; }
    public bool PendingApproval { get; set; }
    public string? CurrentProviderVersion { get; set; }
}

public sealed class TenantDelegationListInput
{
    public string? PermissionName { get; set; }
    public TenantDelegationTargetKind? TargetKind { get; set; }
    public Guid? TargetId { get; set; }
}

public sealed class TenantDelegationOptionDto
{
    [Required]
    public string PermissionName { get; set; } = string.Empty;

    [Required]
    public string DisplayName { get; set; } = string.Empty;

    [Required]
    public string GroupName { get; set; } = string.Empty;

    public bool AllowsUser { get; set; }
    public bool AllowsRole { get; set; }
    public bool AllowsAllow { get; set; }
    public bool AllowsDeny { get; set; }
    public bool RequiresApproval { get; set; }
    public int? MaximumExpirySeconds { get; set; }
}

public sealed class HostTenantDelegationCeilingDto
{
    public Guid TenantId { get; set; }

    [Required]
    public string TargetDisplayName { get; set; } = "TenantAdmin";

    [Required]
    public string ProviderVersion { get; set; } = string.Empty;

    public List<PermissionGroupDto> Groups { get; set; } = [];
}

public sealed class UpdateHostTenantDelegationCeilingInput
{
    [Required]
    public string ExpectedProviderVersion { get; set; } = string.Empty;

    public List<HostTenantDelegationCeilingChangeDto> Changes { get; set; } = [];

    [Required]
    [MaxLength(1024)]
    public string Reason { get; set; } = string.Empty;

    [Required]
    [MaxLength(128)]
    public string TicketId { get; set; } = string.Empty;
}

public sealed class HostTenantDelegationCeilingChangeDto
{
    [Required]
    [MaxLength(256)]
    public string PermissionName { get; set; } = string.Empty;

    public bool IsGranted { get; set; }
    public PermissionGrantEffect Effect { get; set; } = PermissionGrantEffect.Allow;
}

public sealed class HostTenantDelegationCeilingUpdateResult
{
    [Required]
    public string ProviderVersion { get; set; } = string.Empty;

    public List<string> AppliedPermissionNames { get; set; } = [];
    public List<string> PendingApprovalPermissionNames { get; set; } = [];
    public List<string> UnchangedPermissionNames { get; set; } = [];
    public int RevokedDownstreamGrantCount { get; set; }
}

/// <remarks>
/// The annotated members are always populated by <c>TenantDelegationAppService.GetAsync</c>, including
/// <see cref="ProviderVersion"/>, which falls back to the initial version rather than being left unset.
/// Declaring that lets a caller round-trip an entry straight back into a mutation: without it the
/// generated client typed them optional, and a revoke built from an entry could pass an undefined
/// concurrency token and be refused with a conflict it could not explain.
/// </remarks>
public sealed class TenantDelegationEntryDto
{
    [Required]
    public Guid TargetId { get; set; }

    [Required]
    public TenantDelegationTargetKind TargetKind { get; set; }

    [Required]
    public string PermissionName { get; set; } = string.Empty;

    [Required]
    public PermissionGrantEffect Effect { get; set; }

    public DateTime? ExpiresAtUtc { get; set; }
    public Guid? GrantedByUserId { get; set; }
    public string? Reason { get; set; }

    /// <summary>
    /// Optimistic-concurrency token for this target's assignment state, to be echoed back as
    /// <see cref="TenantDelegationMutationInput.ExpectedProviderVersion"/> on a follow-up mutation.
    /// </summary>
    [Required]
    public string ProviderVersion { get; set; } = string.Empty;
}

public interface ITenantDelegationAppService
{
    Task<IReadOnlyList<TenantDelegationEntryDto>> GetAsync(
        TenantDelegationListInput input,
        CancellationToken cancellationToken = default);

    Task<TenantDelegationMutationResult> MutateAsync(
        TenantDelegationMutationInput input,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TenantDelegationOptionDto>> GetOptionsAsync(
        CancellationToken cancellationToken = default);

    Task<HostTenantDelegationCeilingDto> GetHostCeilingAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default);

    Task<HostTenantDelegationCeilingUpdateResult> UpdateHostCeilingAsync(
        Guid tenantId,
        string idempotencyKey,
        UpdateHostTenantDelegationCeilingInput input,
        CancellationToken cancellationToken = default);
}
