namespace Permissions.Application.Contracts.PermissionEvaluation;

/// <summary>
/// Narrow service-to-service request to evaluate a subject's current authority in one tenant.
/// The caller supplies neither role claims nor a token-derived authorization decision: Permissions resolves
/// account eligibility and active roles from Identity at evaluation time.
/// </summary>
public sealed class SubjectPermissionAuthorizationRequestDto
{
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public List<string> Permissions { get; set; } = [];
}

/// <summary>
/// Safe, minimal reauthorization outcome. The snapshot hash is returned only when the subject was eligible for
/// evaluation; it contains no grant names, role names, account data, or personal information.
/// </summary>
public sealed class SubjectPermissionAuthorizationResultDto
{
    public bool IsAuthorized { get; set; }
    public string? SnapshotHash { get; set; }
}

public interface ISubjectPermissionAuthorizationAppService
{
    Task<SubjectPermissionAuthorizationResultDto> AuthorizeCurrentSubjectAsync(
        SubjectPermissionAuthorizationRequestDto input,
        CancellationToken cancellationToken = default);
}
