using System.ComponentModel.DataAnnotations;
using Identity.Application.Contracts.Users;
using Shared;

namespace Identity.Application.Contracts.Invitations;

public class InvitationDto
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string TenantName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? RoleName { get; set; }
    public string? SuggestedUserName { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? AcceptedAtUtc { get; set; }
    public Guid? AcceptedUserId { get; set; }
    public DateTime? CanceledAtUtc { get; set; }
}

/// <remarks>
/// The sortable set mirrors <c>InvitationAppService.ApplySorting</c>, which serves both invitation list
/// inputs. <c>CreatedAtUtc</c> is declared explicitly rather than being reachable only as the fallback
/// order, so a caller can ask for it in either direction.
/// </remarks>
[SortableFields(
    "Email",
    "ExpiresAtUtc",
    "CreatedAtUtc",
    DefaultSort = "CreatedAtUtc desc")]
public class InvitationListInput : PagedRequestDto
{
    public Guid? TenantId { get; set; }

    [MaxLength(256)]
    public string? Email { get; set; }

    [MaxLength(32)]
    public string? Status { get; set; }

    public int? ExpiresWithinDays { get; set; }
}

public class CreateInvitationInput
{
    public Guid? TenantId { get; set; }

    [Required]
    [EmailAddress]
    [MaxLength(256)]
    public string Email { get; set; } = string.Empty;

    [Required]
    [MaxLength(64)]
    public string RoleName { get; set; } = string.Empty;

    [MaxLength(256)]
    public string? SuggestedUserName { get; set; }

    [Required]
    [MaxLength(256)]
    public string AcceptanceBaseUrl { get; set; } = string.Empty;
}

public class ResendInvitationInput
{
    [Required]
    [MaxLength(256)]
    public string AcceptanceBaseUrl { get; set; } = string.Empty;
}

/// <summary>
/// One normalized invitation row for a bulk request. The browser is responsible
/// for parsing the uploaded spreadsheet and mapping the user's arbitrary column
/// headers onto these canonical fields before calling the API; the backend
/// re-validates every row authoritatively. <see cref="RowNumber"/> is the
/// caller's own 1-based row reference used to correlate per-row errors back to
/// the source file (falls back to list position when not supplied).
/// </summary>
public class InvitationImportItem
{
    public int RowNumber { get; set; }

    [MaxLength(256)]
    public string Email { get; set; } = string.Empty;

    [MaxLength(64)]
    public string RoleName { get; set; } = string.Empty;

    [MaxLength(256)]
    public string? SuggestedUserName { get; set; }
}

public class ValidateInvitationBatchInput
{
    public Guid? TenantId { get; set; }

    public List<InvitationImportItem> Items { get; set; } = [];
}

public class CreateInvitationBatchInput
{
    public Guid? TenantId { get; set; }

    [Required]
    [MaxLength(256)]
    public string AcceptanceBaseUrl { get; set; } = string.Empty;

    public List<InvitationImportItem> Items { get; set; } = [];
}

public class InvitationRowErrorDto
{
    public int RowNumber { get; set; }
    public string? Field { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class InvitationBatchValidationDto
{
    public int TotalRowCount { get; set; }
    public int ValidRowCount { get; set; }
    public List<InvitationRowErrorDto> Errors { get; set; } = [];
}

public class InvitationBatchResultDto
{
    public int RequestedCount { get; set; }
    public int CreatedCount { get; set; }
    public int SkippedCount { get; set; }
    public List<InvitationDto> Created { get; set; } = [];
    public List<InvitationRowErrorDto> Errors { get; set; } = [];
}

public sealed class PlatformStaffInvitationDto : InvitationDto
{
    /// <summary>
    /// The request was accepted for notification delivery. This is deliberately
    /// not presented as an email-delivered guarantee.
    /// </summary>
    public string DeliveryState { get; set; } = "Queued";
}

public sealed class CreatePlatformStaffInvitationInput
{
    [Required]
    [EmailAddress]
    [MaxLength(256)]
    public string Email { get; set; } = string.Empty;

    [Required]
    [MaxLength(64)]
    public string RoleName { get; set; } = string.Empty;

    [MaxLength(256)]
    public string? SuggestedUserName { get; set; }
}

/// <remarks>
/// Shares <c>InvitationAppService.ApplySorting</c> with <see cref="InvitationListInput"/>, so the
/// declared set is deliberately identical.
/// </remarks>
[SortableFields(
    "Email",
    "ExpiresAtUtc",
    "CreatedAtUtc",
    DefaultSort = "CreatedAtUtc desc")]
public sealed class PlatformStaffInvitationListInput : PagedRequestDto
{
    [MaxLength(256)]
    public string? Email { get; set; }

    [MaxLength(32)]
    public string? Status { get; set; }

    public int? ExpiresWithinDays { get; set; }
}

public interface IInvitationAppService
{
    Task<PagedResultDto<InvitationDto>> GetListAsync(InvitationListInput input);
    Task<InvitationDto> CreateAsync(CreateInvitationInput input);
    Task<InvitationBatchValidationDto> ValidateBatchAsync(ValidateInvitationBatchInput input);
    Task<InvitationBatchResultDto> CreateBatchAsync(CreateInvitationBatchInput input);
    Task<InvitationDto> ResendAsync(Guid id, ResendInvitationInput input);
    Task CancelAsync(Guid id);
    Task<PagedResultDto<PlatformStaffInvitationDto>> GetPlatformStaffListAsync(PlatformStaffInvitationListInput input);
    Task<PlatformStaffInvitationDto> CreatePlatformStaffAsync(CreatePlatformStaffInvitationInput input);
    Task<PlatformStaffInvitationDto> ResendPlatformStaffAsync(Guid id);
    Task CancelPlatformStaffAsync(Guid id);
}
