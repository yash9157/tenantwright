namespace Permissions.Domain.Shared.Constants;

public static class PermissionProviderKeyNormalizer
{
    public static string Normalize(string providerName, string providerKey)
    {
        if (string.IsNullOrWhiteSpace(providerKey))
        {
            throw new ArgumentException("Provider key is required.", nameof(providerKey));
        }

        var normalized = providerKey.Trim();
        if (providerName == PermissionProviderNames.Role)
        {
            if (!Guid.TryParseExact(normalized, "D", out var roleId) || roleId == Guid.Empty)
            {
                throw new ArgumentException("Role provider keys must be canonical immutable role IDs.", nameof(providerKey));
            }

            return roleId.ToString("D");
        }

        return normalized;
    }
}
