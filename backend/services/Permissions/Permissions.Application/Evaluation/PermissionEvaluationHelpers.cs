using System.Security.Cryptography;
using System.Text;

namespace Permissions.Application.Evaluation;

internal static class PermissionPatternHelper
{
    public static bool IsWildcard(string permissionName)
    {
        if (string.IsNullOrWhiteSpace(permissionName))
        {
            return false;
        }

        permissionName = permissionName.Trim();
        if (permissionName == "*")
        {
            return true;
        }

        return permissionName.EndsWith(".*", StringComparison.Ordinal) || permissionName.EndsWith('*');
    }

    public static bool Matches(string permissionName, string pattern)
    {
        permissionName = permissionName.Trim();
        pattern = pattern.Trim();

        if (pattern == "*")
        {
            return true;
        }

        if (pattern.EndsWith(".*", StringComparison.Ordinal))
        {
            var prefix = pattern[..^1];
            return permissionName.StartsWith(prefix, StringComparison.Ordinal);
        }

        if (pattern.EndsWith('*'))
        {
            var prefix = pattern[..^1];
            return permissionName.StartsWith(prefix, StringComparison.Ordinal);
        }

        return string.Equals(permissionName, pattern, StringComparison.Ordinal);
    }

    public static IEnumerable<string> EnumerateParents(string permissionName)
    {
        permissionName = permissionName.Trim();
        while (true)
        {
            var lastDotIndex = permissionName.LastIndexOf('.');
            if (lastDotIndex <= 0)
            {
                yield break;
            }

            permissionName = permissionName[..lastDotIndex];
            yield return permissionName;
        }
    }
}

internal static class PermissionSnapshotKeyHelper
{
    public static string ComputeRoleIdentityHash(IReadOnlyList<Guid> roleIds)
    {
        var ids = roleIds
            .Where(x => x != Guid.Empty)
            .Distinct()
            .OrderBy(x => x)
            .Select(x => x.ToString("D"));
        return Sha256Hex(string.Join(',', ids));
    }

    public static string BuildCacheKey(Guid userId, string rolesHash, long stamp)
    {
        return $"u:{userId:D}:r:{rolesHash}:s:{stamp}";
    }

    public static string ComputeSnapshotHash(Guid userId, string rolesHash, long stamp)
    {
        return Sha256Hex($"{userId:D}|{rolesHash}|{stamp}");
    }

    private static string Sha256Hex(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
