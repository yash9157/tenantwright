namespace Permissions.Domain.Shared.Constants;

public static class PermissionProviderNameNormalizer
{
    public static string Normalize(string providerName)
    {
        providerName = providerName.Trim();

        if (providerName.Equals(PermissionProviderNames.Role, StringComparison.OrdinalIgnoreCase))
        {
            return PermissionProviderNames.Role;
        }

        if (providerName.Equals(PermissionProviderNames.User, StringComparison.OrdinalIgnoreCase))
        {
            return PermissionProviderNames.User;
        }

        if (providerName.Equals(PermissionProviderNames.Client, StringComparison.OrdinalIgnoreCase))
        {
            return PermissionProviderNames.Client;
        }

        return providerName;
    }
}
