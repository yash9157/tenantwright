using Permissions.Application.PermissionManagement;
using Permissions.Domain.Grants;

namespace Permissions.Tests.TestDoubles;

internal sealed class NoopPermissionApprovalDisplayProjectionResolver : IPermissionApprovalDisplayProjectionResolver
{
    public Task<IReadOnlyDictionary<Guid, PermissionApprovalDisplayProjection>> ResolveAsync(
        IReadOnlyCollection<PermissionGrantChangeRequest> requests,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyDictionary<Guid, PermissionApprovalDisplayProjection>>(
            new Dictionary<Guid, PermissionApprovalDisplayProjection>());
}
