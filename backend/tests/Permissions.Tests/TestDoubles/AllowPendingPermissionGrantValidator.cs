using Permissions.Application.PermissionManagement;
using Permissions.Domain.Grants;

namespace Permissions.Tests.TestDoubles;

internal sealed class AllowPendingPermissionGrantValidator : IPendingPermissionGrantValidator
{
    public Task ValidateAsync(
        PermissionGrantChangeRequest request,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
