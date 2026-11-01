namespace Identity.Application.ApplicationUsers;

public static class TenantWrightIdentityRoleNames
{
    public const string HostAdmin = "admin";
    public const string TenantAdmin = "TenantAdmin";

    public static string Normalize(string? roleName)
    {
        var normalized = roleName?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        if (string.Equals(normalized, TenantAdmin, StringComparison.OrdinalIgnoreCase))
        {
            return TenantAdmin;
        }

        if (string.Equals(normalized, HostAdmin, StringComparison.OrdinalIgnoreCase))
        {
            return HostAdmin;
        }

        return normalized;
    }

    public static IReadOnlyList<string> NormalizeMany(IEnumerable<string>? roleNames)
    {
        if (roleNames == null)
        {
            return [];
        }

        return roleNames
            .Select(Normalize)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool IsHostAdminRole(string? roleName)
        => string.Equals(Normalize(roleName), HostAdmin, StringComparison.Ordinal);

    public static bool IsTenantAdminRole(string? roleName)
        => string.Equals(Normalize(roleName), TenantAdmin, StringComparison.Ordinal);

    public static bool IsElevatedTenantRole(string? roleName)
        => IsHostAdminRole(roleName) || IsTenantAdminRole(roleName);
}
