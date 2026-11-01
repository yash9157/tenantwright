namespace Permissions.Domain.Shared.Constants;

public static class FeatureProviderNames
{
    public const string Tenant = "T";

    public static bool IsSupported(string providerName) =>
        string.Equals(providerName, Tenant, StringComparison.Ordinal);
}
