using Permissions.Application.Contracts.Common;
using Permissions.Domain.Shared.Enums;

namespace Permissions.Application.Contracts.PermissionApproval;

/// <summary>
/// Flow 6 approval workflow. Granting a permission whose definition is marked <c>RequiresApproval</c>
/// creates a Pending request (via the assignment path); a different admin holding
/// <c>Permissions.Management.Approve</c> approves or rejects it, and the requester may cancel it.
/// </summary>
public interface IPermissionApprovalAppService
{
    Task<PagedResultDto<PermissionGrantChangeRequestDto>> GetListAsync(
        PermissionGrantChangeRequestListInput? input = null,
        CancellationToken cancellationToken = default);

    Task<PagedResultDto<PermissionGrantChangeRequestDto>> GetMineAsync(
        PermissionGrantChangeRequestListInput? input = null,
        CancellationToken cancellationToken = default);

    Task<PermissionApprovalPendingCountDto> GetPendingCountAsync(CancellationToken cancellationToken = default);

    Task<PermissionGrantChangeRequestDto> ApproveAsync(
        Guid id,
        DecideGrantChangeRequestInput input,
        CancellationToken cancellationToken = default);

    Task<PermissionGrantChangeRequestDto> RejectAsync(
        Guid id,
        DecideGrantChangeRequestInput input,
        CancellationToken cancellationToken = default);

    Task<PermissionGrantChangeRequestDto> CancelAsync(
        Guid id,
        CancelGrantChangeRequestInput input,
        CancellationToken cancellationToken = default);

    Task<PermissionGrantChangeRequestDto> CancelAnyAsync(
        Guid id,
        CancelGrantChangeRequestInput input,
        CancellationToken cancellationToken = default);

    Task<PermissionApprovalBulkDecisionResultDto> DecideBulkAsync(
        PermissionApprovalBulkDecisionInput input,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Tab/scope selector for the approval inbox list.
/// </summary>
public enum PermissionGrantChangeRequestScope
{
    /// <summary>Only pending requests, oldest first (default; preserves prior behavior).</summary>
    Pending = 0,

    /// <summary>Requests created by the current user, any status, newest first.</summary>
    Mine = 1,

    /// <summary>Decided requests (Applied/Rejected/Cancelled), newest decision first.</summary>
    History = 2,

    /// <summary>All in-tenant requests regardless of status, newest first.</summary>
    All = 3
}

public sealed class PermissionGrantChangeRequestListInput
{
    /// <summary>Which inbox view to return. Defaults to <see cref="PermissionGrantChangeRequestScope.Pending"/>.</summary>
    public PermissionGrantChangeRequestScope Scope { get; set; } = PermissionGrantChangeRequestScope.Pending;

    /// <summary>Optional explicit status filter applied on top of the scope.</summary>
    public PermissionGrantChangeRequestStatus? Status { get; set; }

    /// <summary>Optional target provider filter (e.g. only requests for a given user/role).</summary>
    public string? ProviderName { get; set; }

    public string? ProviderKey { get; set; }

    public string? PermissionName { get; set; }

    public Guid? RequestedByUserId { get; set; }

    public int SkipCount { get; set; }

    public int MaxResultCount { get; set; } = 200;
}

public sealed class DecideGrantChangeRequestInput
{
    public string? DecisionReason { get; set; }
    public long ExpectedVersion { get; set; }
    public string IdempotencyKey { get; set; } = default!;
}

public sealed class CancelGrantChangeRequestInput
{
    public string? Reason { get; set; }
    public long ExpectedVersion { get; set; }
    public string IdempotencyKey { get; set; } = default!;
}

public sealed class PermissionApprovalPendingCountDto
{
    public long Count { get; set; }
}

public enum PermissionApprovalConflictCode
{
    VersionConflict = 1,
    AlreadyDecided = 2,
    ConcurrentDecision = 3,
    IdempotencyKeyReused = 4
}

public sealed class PermissionApprovalConflictDto
{
    public PermissionApprovalConflictCode Code { get; set; }
    public long? CurrentVersion { get; set; }
    public PermissionGrantChangeRequestStatus? CurrentStatus { get; set; }
}

public enum PermissionApprovalDecision
{
    Approve = 1,
    Reject = 2,
    Cancel = 3
}

public sealed class PermissionApprovalBulkDecisionInput
{
    public string? IdempotencyKey { get; set; }
    public List<PermissionApprovalBulkDecisionItemInput> Items { get; set; } = [];
}

public sealed class PermissionApprovalBulkDecisionItemInput
{
    public Guid Id { get; set; }
    public long ExpectedVersion { get; set; }
    public PermissionApprovalDecision Decision { get; set; }
    public string? DecisionReason { get; set; }
}

public sealed class PermissionApprovalBulkDecisionResultDto
{
    public List<PermissionGrantChangeRequestDto> Items { get; set; } = [];
    public bool Replayed { get; set; }
}

public sealed class PermissionGrantChangeRequestDto
{
    public Guid Id { get; set; }

    public Guid? TenantId { get; set; }

    public string PermissionName { get; set; } = default!;

    public string ProviderName { get; set; } = default!;

    public string ProviderKey { get; set; } = default!;

    public PermissionGrantEffect RequestedEffect { get; set; }

    public DateTime? ExpiresAtUtc { get; set; }

    public string? Reason { get; set; }

    public string? TicketId { get; set; }

    public Guid? RequestedByUserId { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public PermissionGrantChangeRequestStatus Status { get; set; }

    public Guid? DecidedByUserId { get; set; }

    public string? DecisionReason { get; set; }

    public DateTime? DecidedAtUtc { get; set; }

    public long Version { get; set; }

    public string PermissionDisplayName { get; set; } = default!;

    public string ProviderDisplayName { get; set; } = default!;

    public string RequesterDisplayName { get; set; } = default!;

    public string? ApproverDisplayName { get; set; }
}
