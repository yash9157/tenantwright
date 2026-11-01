using Permissions.Application.Contracts.PermissionEvaluation;
using Permissions.Application.PermissionManagement;
using Permissions.Domain.Shared.Constants;
using Shared.Modularity.DependencyInjection;

namespace Permissions.Application.PermissionEvaluation;

/// <summary>
/// Reauthorizes one subject using its current Identity account state and active immutable role IDs.
/// This is deliberately separate from request authorization: durable workers have no trustworthy HTTP principal
/// or queued role claims to replay.
/// </summary>
[ExposeServices(typeof(ISubjectPermissionAuthorizationAppService))]
internal sealed class CurrentSubjectPermissionAuthorizationAppService :
    ISubjectPermissionAuthorizationAppService,
    ITransientDependency
{
    private readonly IIdentityPermissionTargetValidator _identityTargets;
    private readonly PermissionAuthorizationEvaluator _evaluator;

    public CurrentSubjectPermissionAuthorizationAppService(
        IIdentityPermissionTargetValidator identityTargets,
        PermissionAuthorizationEvaluator evaluator)
    {
        _identityTargets = identityTargets;
        _evaluator = evaluator;
    }

    public async Task<SubjectPermissionAuthorizationResultDto> AuthorizeCurrentSubjectAsync(
        SubjectPermissionAuthorizationRequestDto input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.TenantId == Guid.Empty || input.UserId == Guid.Empty)
            throw new global::Shared.SharedValidationException("A tenant-qualified subject is required.");

        var permissionNames = NormalizePermissionNames(input.Permissions);
        var subject = await _identityTargets.GetUserAsync(input.UserId, input.TenantId, cancellationToken);
        if (!subject.IsActive || subject.IsLockedOut)
        {
            return new SubjectPermissionAuthorizationResultDto
            {
                IsAuthorized = false
            };
        }

        var evaluation = await _evaluator.EvaluateAsync(
            input.TenantId,
            input.UserId,
            subject.RoleIds,
            permissionNames,
            cancellationToken);
        return new SubjectPermissionAuthorizationResultDto
        {
            IsAuthorized = evaluation.Results.Count == permissionNames.Length &&
                           evaluation.Results.All(result => result.IsGranted),
            SnapshotHash = evaluation.SnapshotHash
        };
    }

    private static string[] NormalizePermissionNames(IReadOnlyCollection<string>? permissions)
    {
        if (permissions is not { Count: > 0 and <= 8 })
            throw new global::Shared.SharedValidationException("One to eight permission names are required.");

        var names = permissions
            .Where(permissionName => !string.IsNullOrWhiteSpace(permissionName))
            .Select(permissionName => permissionName.Trim())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (names.Length == 0 || names.Length > 8 || names.Any(name => name.Length > PermissionsConsts.MaxPermissionNameLength))
            throw new global::Shared.SharedValidationException("One or more permission names are invalid.");

        return names;
    }
}
