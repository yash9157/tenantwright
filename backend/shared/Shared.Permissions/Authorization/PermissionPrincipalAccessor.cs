using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace Shared.Permissions.Authorization;

internal static class PermissionPrincipalAccessor
{
    public static Guid? FindTenantId(ClaimsPrincipal principal)
    {
        var tenantId =
            principal.Claims.FirstOrDefault(c => c.Type == "tenantid" || c.Type.EndsWith("/tenantid", StringComparison.OrdinalIgnoreCase))
                ?.Value;

        return Guid.TryParse(tenantId, out var guid) ? guid : null;
    }

    public static Guid? FindUserId(ClaimsPrincipal principal)
    {
        var userId =
            principal.Claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value
            ?? principal.Claims.FirstOrDefault(c => c.Type == "sub")?.Value;

        return Guid.TryParse(userId, out var guid) ? guid : null;
    }

    public static string ComputeRoleIdentityHash(IReadOnlyList<Guid> roleIds)
    {
        var canonicalIds = roleIds.Distinct().OrderBy(x => x).Select(x => x.ToString("D"));
        var payload = string.Join(',', canonicalIds);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
