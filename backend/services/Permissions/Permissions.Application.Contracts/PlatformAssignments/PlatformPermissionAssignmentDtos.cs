using Permissions.Application.Contracts.PermissionManagement;
using Permissions.Domain.Shared.Enums;

namespace Permissions.Application.Contracts.PlatformAssignments;

public enum PermissionAssignmentTargetType
{
    User = 0,
    Role = 1
}

public sealed class PermissionAssignmentTargetDto
{
    public PermissionAssignmentTargetType Type { get; set; }
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = default!;
}

public sealed class PlatformPermissionAssignmentDto
{
    public PermissionAssignmentTargetDto Target { get; set; } = new();
    public string ProviderVersion { get; set; } = default!;
    public List<PermissionGroupDto> Groups { get; set; } = [];
    public List<PendingPermissionAssignmentDto> PendingApprovals { get; set; } = [];
}

public sealed class PendingPermissionAssignmentDto
{
    public Guid RequestId { get; set; }
    public string PermissionName { get; set; } = default!;
    public PermissionGrantEffect Effect { get; set; }
    public DateTime RequestedAtUtc { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
    public string? Reason { get; set; }
    public string? TicketId { get; set; }
}

public sealed class UpdatePlatformPermissionAssignmentRequest
{
    public string ExpectedProviderVersion { get; set; } = default!;
    public List<PlatformPermissionAssignmentChangeDto> Changes { get; set; } = [];
}

public sealed class PlatformPermissionAssignmentChangeDto
{
    public string Name { get; set; } = default!;
    public bool IsGranted { get; set; }
    public PermissionGrantEffect Effect { get; set; } = PermissionGrantEffect.Allow;
    public DateTime? ExpiresAtUtc { get; set; }
    public string? Reason { get; set; }
    public string? TicketId { get; set; }
}

public sealed class PermissionAssignmentUpdateResultDto
{
    public string ProviderVersion { get; set; } = default!;
    public List<PermissionAssignmentChangeResultDto> Applied { get; set; } = [];
    public List<PendingPermissionAssignmentChangeResultDto> PendingApproval { get; set; } = [];
    public List<PermissionAssignmentChangeResultDto> Unchanged { get; set; } = [];
}

public class PermissionAssignmentChangeResultDto
{
    public string PermissionName { get; set; } = default!;
    public bool IsGranted { get; set; }
    public PermissionGrantEffect Effect { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
    public string? Reason { get; set; }
    public string? TicketId { get; set; }
}

public sealed class PendingPermissionAssignmentChangeResultDto : PermissionAssignmentChangeResultDto
{
    public Guid RequestId { get; set; }
}

public enum PermissionAssignmentConflictCode
{
    ProviderVersionConflict = 0,
    AssignmentConflict = 1,
    IdempotencyKeyReused = 2,
    IdempotencyOperationInProgress = 3,
    ConcurrentWriteConflict = 4
}

public sealed class PermissionAssignmentConflictDto
{
    public PermissionAssignmentConflictCode Code { get; set; }
    public string? CurrentProviderVersion { get; set; }
    public List<string> ConflictingPermissionNames { get; set; } = [];
}
