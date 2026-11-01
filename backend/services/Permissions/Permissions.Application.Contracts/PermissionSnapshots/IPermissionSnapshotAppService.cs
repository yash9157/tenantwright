using Permissions.Application.Contracts.PermissionEvaluation;

namespace Permissions.Application.Contracts.PermissionSnapshots;

public interface IPermissionSnapshotAppService
{
    Task<PermissionSnapshotDto> GetSnapshotAsync(Guid? tenantId, Guid userId, List<Guid> roleIds, CancellationToken cancellationToken = default);
    Task<PermissionSnapshotDto> GetInternalSnapshotAsync(Guid? tenantId, Guid userId, List<Guid> roleIds, CancellationToken cancellationToken = default);
}
