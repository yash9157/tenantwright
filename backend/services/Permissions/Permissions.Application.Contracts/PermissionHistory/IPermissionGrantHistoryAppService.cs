using Permissions.Domain.Shared.Enums;

namespace Permissions.Application.Contracts.PermissionHistory;

/// <summary>
/// Read-only, first-class permission grant-change history (gap claim 8). Backed by an append-only
/// projection recorded at every mutation, so it answers "who granted/revoked permission X to
/// principal Y, when, and why" — including changes to grants that have since expired or been removed.
/// The primary access pattern is history for one principal (e.g. a user), via
/// <see cref="GetPermissionGrantHistoryInput.ProviderName"/> + <see cref="GetPermissionGrantHistoryInput.ProviderKey"/>.
/// </summary>
public interface IPermissionGrantHistoryAppService
{
    /// <summary>
    /// Returns history scoped to the caller's ambient tenant context. This is the existing
    /// tenant-safe API and must not be used as a platform-wide query.
    /// </summary>
    Task<PermissionGrantHistoryResultDto> GetListAsync(
        GetPermissionGrantHistoryInput input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns history across host and tenant scopes for the dedicated host-only platform endpoint.
    /// Its controller is responsible for enforcing the host role, exact capability, and host context
    /// before this cross-tenant query is reached.
    /// </summary>
    Task<PermissionGrantHistoryResultDto> GetPlatformListAsync(
        GetPermissionGrantHistoryInput input,
        CancellationToken cancellationToken = default);
}

public sealed class GetPermissionGrantHistoryInput
{
    /// <summary>Principal provider filter (e.g. "R" for role, "U" for user).</summary>
    public string? ProviderName { get; set; }

    /// <summary>Principal key filter (role name or user id).</summary>
    public string? ProviderKey { get; set; }

    public string? PermissionName { get; set; }

    public PermissionGrantHistoryChangeType? ChangeType { get; set; }

    public Guid? ActorUserId { get; set; }

    public DateTime? FromUtc { get; set; }

    public DateTime? ToUtc { get; set; }

    public int SkipCount { get; set; }

    public int MaxResultCount { get; set; } = 100;
}

public sealed class PermissionGrantHistoryResultDto
{
    public long TotalCount { get; set; }

    public List<PermissionGrantHistoryEntryDto> Items { get; set; } = [];
}

public sealed class PermissionGrantHistoryEntryDto
{
    public Guid Id { get; set; }

    public Guid? TenantId { get; set; }

    public string ProviderName { get; set; } = default!;

    public string ProviderKey { get; set; } = default!;

    public string PermissionName { get; set; } = default!;

    public string? ResourceName { get; set; }

    public string? ResourceKey { get; set; }

    public long? ResourceOwnerRevision { get; set; }

    public PermissionGrantHistoryChangeType ChangeType { get; set; }

    public PermissionGrantEffect? OldEffect { get; set; }

    public PermissionGrantEffect? NewEffect { get; set; }

    public DateTime? ExpiresAtUtc { get; set; }

    public string? Reason { get; set; }

    public string? TicketId { get; set; }

    public Guid? ActorUserId { get; set; }

    public DateTime OccurredAtUtc { get; set; }
}
