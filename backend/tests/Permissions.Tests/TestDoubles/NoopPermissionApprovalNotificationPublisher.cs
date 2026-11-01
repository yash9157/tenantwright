using Permissions.Application.PermissionManagement;
using Permissions.Domain.Grants;

namespace Permissions.Tests.TestDoubles;

internal sealed class NoopPermissionApprovalNotificationPublisher : IPermissionApprovalNotificationPublisher
{
    public Task RequestedAsync(PermissionGrantChangeRequest request, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task DecidedAsync(PermissionGrantChangeRequest request, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
