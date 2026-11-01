using Permissions.Domain.Shared.Enums;

namespace Permissions.Application.Contracts.PermissionConflictRules;

/// <summary>An applied separation-of-duty rule.</summary>
public class PermissionConflictRuleDto
{
    public Guid Id { get; set; }
    public string PermissionA { get; set; } = default!;
    public string PermissionB { get; set; } = default!;
    public string? Reason { get; set; }
    public DateTime CreationTimeUtc { get; set; }
    public DateTime? LastModifiedTimeUtc { get; set; }
    public long Version { get; set; }
}

/// <summary>A four-eyes request to change the rule set, with display projections for requester/decider.</summary>
public class PermissionConflictRuleChangeRequestDto
{
    public Guid Id { get; set; }
    public PermissionConflictRuleChangeType ChangeType { get; set; }
    public Guid? TargetRuleId { get; set; }
    public long? ExpectedRuleVersion { get; set; }
    public string PermissionA { get; set; } = default!;
    public string PermissionB { get; set; } = default!;
    public string? RuleReason { get; set; }
    public string? RequestReason { get; set; }
    public string? TicketId { get; set; }
    public Guid? RequestedByUserId { get; set; }
    public string? RequestedByDisplayName { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public PermissionConflictRuleChangeStatus Status { get; set; }
    public Guid? DecidedByUserId { get; set; }
    public string? DecidedByDisplayName { get; set; }
    public string? DecisionReason { get; set; }
    public DateTime? DecidedAtUtc { get; set; }
    public long Version { get; set; }
}

public class PagedPermissionConflictRuleChangeRequestsDto
{
    public long TotalCount { get; set; }
    public List<PermissionConflictRuleChangeRequestDto> Items { get; set; } = [];
}

/// <summary>Blast-radius preview: how many current targets already hold both sides of the pair.</summary>
public class PermissionConflictRuleImpactDto
{
    public string PermissionA { get; set; } = default!;
    public string PermissionB { get; set; } = default!;
    public int ViolatingTargetCount { get; set; }
    public List<PermissionConflictRuleImpactTargetDto> ViolatingTargets { get; set; } = [];
}

public class PermissionConflictRuleImpactTargetDto
{
    public string ProviderName { get; set; } = default!;
    public string ProviderKey { get; set; } = default!;
    public string? DisplayName { get; set; }
}

public class RequestCreatePermissionConflictRuleDto
{
    public string PermissionA { get; set; } = default!;
    public string PermissionB { get; set; } = default!;
    public string? RuleReason { get; set; }
    public string? RequestReason { get; set; }
    public string? TicketId { get; set; }
}

public class RequestUpdatePermissionConflictRuleDto
{
    public long ExpectedRuleVersion { get; set; }
    public string PermissionA { get; set; } = default!;
    public string PermissionB { get; set; } = default!;
    public string? RuleReason { get; set; }
    public string? RequestReason { get; set; }
    public string? TicketId { get; set; }
}

public class RequestDeletePermissionConflictRuleDto
{
    public long ExpectedRuleVersion { get; set; }
    public string? RequestReason { get; set; }
    public string? TicketId { get; set; }
}

public class DecidePermissionConflictRuleChangeDto
{
    public long ExpectedRequestVersion { get; set; }
    public string? DecisionReason { get; set; }
}

public class ConflictRuleChangeRequestListInput
{
    public PermissionConflictRuleChangeStatus? Status { get; set; }
    public int SkipCount { get; set; }
    public int MaxResultCount { get; set; } = 50;
}
