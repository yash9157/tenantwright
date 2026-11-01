using System.Security.Claims;

namespace Shared.Permissions.Authorization;

public interface IPermissionChecker
{
    Task<bool> IsGrantedAsync(ClaimsPrincipal principal, string permissionName, CancellationToken cancellationToken = default);

    Task<Dictionary<string, bool>> IsGrantedAsync(
        ClaimsPrincipal principal,
        IReadOnlyList<string> permissionNames,
        CancellationToken cancellationToken = default);
}
