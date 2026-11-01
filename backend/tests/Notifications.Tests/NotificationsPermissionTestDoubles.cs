using System.Security.Claims;
using Shared.Permissions.Authorization;

namespace Notifications.Tests;

internal sealed class NotificationsGrantedPermissionChecker(params string[] grantedPermissions)
    : IPermissionChecker
{
    private readonly HashSet<string> _grants = grantedPermissions.ToHashSet(StringComparer.Ordinal);

    public Task<bool> IsGrantedAsync(
        ClaimsPrincipal principal,
        string permissionName,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_grants.Contains(permissionName));

    public Task<Dictionary<string, bool>> IsGrantedAsync(
        ClaimsPrincipal principal,
        IReadOnlyList<string> permissionNames,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(permissionNames.ToDictionary(
            permission => permission,
            permission => _grants.Contains(permission),
            StringComparer.Ordinal));
}
