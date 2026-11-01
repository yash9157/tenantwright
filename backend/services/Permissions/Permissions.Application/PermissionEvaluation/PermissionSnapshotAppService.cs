using Permissions.Application.Contracts.Authorization;
using Permissions.Application.Contracts.FeatureManagement;
using Permissions.Application.Contracts.PermissionCatalog;
using Permissions.Application.Contracts.PermissionEvaluation;
using Permissions.Application.Contracts.PermissionManagement;
using Permissions.Application.Contracts.ResourcePermissionManagement;
using Permissions.Application.Contracts.TenantPermissionAccess;
using Permissions.Application.Contracts.Features;
using Permissions.Application.Contracts.PermissionSnapshots;
using Permissions.Application.Contracts.Provisioning;
using Permissions.Application.Evaluation;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;

using Shared.AuditLogs.Auditing;
namespace Permissions.Application.PermissionEvaluation;

public class PermissionSnapshotAppService : IPermissionSnapshotAppService, ITransientDependency
{
    private readonly IRequestAuthorizationService _authorization;
    private readonly ICurrentTenant _currentTenant;
    private readonly PermissionSnapshotBuilder _snapshotBuilder;

    public PermissionSnapshotAppService(IRequestAuthorizationService authorization, ICurrentTenant currentTenant, PermissionSnapshotBuilder snapshotBuilder)
    {
        _authorization = authorization;
        _currentTenant = currentTenant;
        _snapshotBuilder = snapshotBuilder;
    }

    public async Task<PermissionSnapshotDto> GetSnapshotAsync(Guid? tenantId, Guid userId, List<Guid> roleIds, CancellationToken cancellationToken = default)
    {
        await _authorization.EnsureAuthorizedAsync(PermissionsPolicies.ReadSnapshots, cancellationToken);
        return await BuildSnapshotAsync(tenantId, userId, roleIds, cancellationToken);
    }

    public async Task<PermissionSnapshotDto> GetInternalSnapshotAsync(Guid? tenantId, Guid userId, List<Guid> roleIds, CancellationToken cancellationToken = default)
    {
        await _authorization.EnsureAuthorizedAsync(PermissionsPolicies.CheckFeaturesInternal, cancellationToken);
        return await BuildSnapshotAsync(tenantId, userId, roleIds, cancellationToken);
    }

    private async Task<PermissionSnapshotDto> BuildSnapshotAsync(Guid? tenantId, Guid userId, List<Guid> roleIds, CancellationToken cancellationToken)
    {
        var effectiveTenantId = ResolveTenantScope(tenantId);
        var (snapshot, snapshotHash) = await _snapshotBuilder.GetOrBuildAsync(effectiveTenantId, userId, roleIds, cancellationToken);

        return new PermissionSnapshotDto
        {
            TenantId = effectiveTenantId,
            UserId = userId,
            SnapshotHash = snapshotHash,
            CreatedAtUtc = snapshot.CreatedAtUtc,
            AllowedExactCount = snapshot.UserAllowExact.Length + snapshot.RoleAllowExact.Length,
            DeniedExactCount = snapshot.UserDenyExact.Length + snapshot.RoleDenyExact.Length,
            AllowedWildcardCount = snapshot.UserAllowWildcard.Length + snapshot.RoleAllowWildcard.Length,
            DeniedWildcardCount = snapshot.UserDenyWildcard.Length + snapshot.RoleDenyWildcard.Length
        };
    }

    private Guid? ResolveTenantScope(Guid? requestedTenantId)
    {
        var currentTenantId = _currentTenant.Id;
        if (!currentTenantId.HasValue)
        {
            return requestedTenantId;
        }

        if (requestedTenantId.HasValue && requestedTenantId.Value != currentTenantId.Value)
        {
            throw new UnauthorizedAccessException("Cross-tenant permission snapshot access is not allowed.");
        }

        return currentTenantId;
    }
}
