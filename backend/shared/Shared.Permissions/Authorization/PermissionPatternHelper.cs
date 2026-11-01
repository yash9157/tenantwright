namespace Shared.Permissions.Authorization;

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
