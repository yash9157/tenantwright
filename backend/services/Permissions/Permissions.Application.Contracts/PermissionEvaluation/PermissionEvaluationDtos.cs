using Permissions.Domain.Shared.Constants;
using Permissions.Domain.Shared.Enums;

namespace Permissions.Application.Contracts.PermissionEvaluation;

public class EvaluatePermissionsRequestDto
{
    public Guid? TenantId { get; set; }
    public Guid UserId { get; set; }
    public List<Guid> RoleIds { get; set; } = new();
    public List<string> Permissions { get; set; } = new();
}

public class EvaluatePermissionsResultDto
{
    public Guid? TenantId { get; set; }
    public Guid UserId { get; set; }
    public List<PermissionEvaluationResultItemDto> Results { get; set; } = new();
    public string SnapshotHash { get; set; } = default!;
}

public class PermissionEvaluationResultItemDto
{
    public string Name { get; set; } = default!;
    public bool IsGranted { get; set; }
    public PermissionDecisionSource Source { get; set; }
}

public class PermissionSnapshotDto
{
    public Guid? TenantId { get; set; }
    public Guid UserId { get; set; }
    public string SnapshotHash { get; set; } = default!;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public int AllowedExactCount { get; set; }
    public int DeniedExactCount { get; set; }
    public int AllowedWildcardCount { get; set; }
    public int DeniedWildcardCount { get; set; }
}

public class GetMyPermissionsResultDto
{
    public Guid? TenantId { get; set; }
    public Guid UserId { get; set; }
    public List<string> Permissions { get; set; } = new();
}

public sealed class ExplainMyPermissionRequestDto
{
    public string PermissionName { get; set; } = default!;
}

public class ExplainPermissionRequestDto
{
    public Guid? TenantId { get; set; }
    public Guid UserId { get; set; }
    public List<Guid> RoleIds { get; set; } = new();
    public string PermissionName { get; set; } = default!;
}

/// <summary>
/// The "why did/didn't this user get this permission" answer for a single permission: whether it was
/// granted, which rule category decided it (<see cref="Source"/>), a human-readable label for that
/// category, and the specific literal or wildcard grant name that matched (<c>null</c> when nothing
/// matched, i.e. <see cref="PermissionDecisionSource.NotGranted"/>).
/// </summary>
public class ExplainPermissionResultDto
{
    public Guid? TenantId { get; set; }
    public Guid UserId { get; set; }
    public string PermissionName { get; set; } = default!;
    public bool IsGranted { get; set; }
    public PermissionDecisionSource Source { get; set; }
    public string SourceLabel { get; set; } = default!;
    public string? MatchedRule { get; set; }
    public string SnapshotHash { get; set; } = default!;
    public List<PermissionExplanationStepDto> Steps { get; set; } = new();
}

public enum PermissionExplanationStepType
{
    Definition = 0,
    GrantDecision = 1,
    Conflict = 2,
    Feature = 3,
    FinalDecision = 4
}

public class PermissionExplanationStepDto
{
    public PermissionExplanationStepType Type { get; set; }
    public bool Passed { get; set; }
    public PermissionDecisionSource Source { get; set; }
    public string SourceLabel { get; set; } = default!;
    public string? MatchedRule { get; set; }
}
