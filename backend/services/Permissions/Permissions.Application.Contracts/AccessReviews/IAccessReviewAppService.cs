using Permissions.Domain.Shared.Enums;

namespace Permissions.Application.Contracts.AccessReviews;

public interface IAccessReviewAppService
{
    Task<AccessReviewCampaignDto> CreateCampaignAsync(
        CreateAccessReviewCampaignInput input,
        CancellationToken cancellationToken = default);

    Task<PagedAccessReviewCampaignResultDto> GetCampaignsAsync(
        GetAccessReviewCampaignsInput input,
        CancellationToken cancellationToken = default);

    Task<PagedAccessReviewItemResultDto> GetItemsAsync(
        Guid campaignId,
        GetAccessReviewItemsInput input,
        CancellationToken cancellationToken = default);

    Task<AccessReviewItemDto> DecideItemAsync(
        Guid campaignId,
        Guid itemId,
        DecideAccessReviewItemInput input,
        CancellationToken cancellationToken = default);

    Task<AccessReviewCampaignDto> CloseCampaignAsync(
        Guid campaignId,
        CloseAccessReviewCampaignInput input,
        CancellationToken cancellationToken = default);

    Task<RolePrivilegeMatrixDto> GetRolePrivilegeMatrixAsync(
        GetRolePrivilegeMatrixInput input,
        CancellationToken cancellationToken = default);
}

public sealed class CreateAccessReviewCampaignInput
{
    public Guid TenantId { get; set; }
    public string Name { get; set; } = default!;
    public DateTime DueAtUtc { get; set; }
}

public sealed class GetAccessReviewCampaignsInput
{
    public Guid TenantId { get; set; }
    public AccessReviewCampaignStatus? Status { get; set; }
    public int SkipCount { get; set; }
    public int MaxResultCount { get; set; } = 50;
}

public sealed class GetAccessReviewItemsInput
{
    public Guid TenantId { get; set; }
    public AccessReviewItemStatus? Status { get; set; }
    public int SkipCount { get; set; }
    public int MaxResultCount { get; set; } = 50;
}

public sealed class DecideAccessReviewItemInput
{
    public Guid TenantId { get; set; }
    public AccessReviewDecision Decision { get; set; }
    public string DecisionReason { get; set; } = default!;
    public long ExpectedVersion { get; set; }
}

public sealed class CloseAccessReviewCampaignInput
{
    public Guid TenantId { get; set; }
}

public sealed class GetRolePrivilegeMatrixInput
{
    public Guid TenantId { get; set; }
    public int MaxRoles { get; set; } = 100;
}

public sealed class PagedAccessReviewCampaignResultDto
{
    public long TotalCount { get; set; }
    public List<AccessReviewCampaignDto> Items { get; set; } = [];
}

public sealed class PagedAccessReviewItemResultDto
{
    public long TotalCount { get; set; }
    public List<AccessReviewItemDto> Items { get; set; } = [];
}

public sealed class AccessReviewCampaignDto
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Name { get; set; } = default!;
    public DateTime SnapshotAtUtc { get; set; }
    public DateTime DueAtUtc { get; set; }
    public AccessReviewCampaignStatus Status { get; set; }
    public DateTime? ClosedAtUtc { get; set; }
    public long PendingItemCount { get; set; }
    public long CertifiedItemCount { get; set; }
    public long RevokedItemCount { get; set; }
    public long ExpiredItemCount { get; set; }
}

public sealed class AccessReviewItemDto
{
    public Guid Id { get; set; }
    public Guid CampaignId { get; set; }
    public Guid SourceGrantId { get; set; }
    public string ProviderName { get; set; } = default!;
    public string ProviderKey { get; set; } = default!;
    public string ProviderDisplayName { get; set; } = default!;
    public string PermissionName { get; set; } = default!;
    public string PermissionGroup { get; set; } = default!;
    public PermissionGrantEffect Effect { get; set; }
    public DateTime SourceGrantCreationTimeUtc { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
    public string? GrantReason { get; set; }
    public string? TicketId { get; set; }
    public AccessReviewItemStatus Status { get; set; }
    public bool HasChangedSinceSnapshot { get; set; }
    public Guid? DecidedByUserId { get; set; }
    public DateTime? DecidedAtUtc { get; set; }
    public string? DecisionReason { get; set; }
    public long Version { get; set; }
}

public sealed class RolePrivilegeMatrixDto
{
    public Guid TenantId { get; set; }
    public DateTime GeneratedAtUtc { get; set; }
    public List<RolePrivilegeMatrixGroupDto> Groups { get; set; } = [];
    public List<RolePrivilegeMatrixRoleDto> Roles { get; set; } = [];
}

public sealed class RolePrivilegeMatrixGroupDto
{
    public string Name { get; set; } = default!;
    public string DisplayName { get; set; } = default!;
    public int PermissionCount { get; set; }
}

public sealed class RolePrivilegeMatrixRoleDto
{
    public Guid RoleId { get; set; }
    public string RoleName { get; set; } = default!;
    public List<RolePrivilegeMatrixCellDto> Cells { get; set; } = [];
}

public sealed class RolePrivilegeMatrixCellDto
{
    public string GroupName { get; set; } = default!;
    public int GrantedCount { get; set; }
    public int GroupTotal { get; set; }
}

public enum AccessReviewConflictCode
{
    VersionConflict = 1,
    ConcurrentDecision = 2
}

public sealed class AccessReviewConflictDto
{
    public AccessReviewConflictCode Code { get; set; }
    public long? CurrentVersion { get; set; }
    public AccessReviewItemStatus? CurrentStatus { get; set; }
}

public sealed class AccessReviewConflictException : Exception
{
    public AccessReviewConflictException(AccessReviewConflictDto conflict)
        : base("The access review item changed. Refresh the campaign evidence before deciding.")
    {
        Conflict = conflict;
    }

    public AccessReviewConflictDto Conflict { get; }
}
