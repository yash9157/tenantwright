namespace Shared.DistributedLocking;

internal enum DistributedLockProviderType
{
    Valkey = 0
}

internal static class DistributedLockingEnvironmentGuard
{
    public static bool IsDevelopment()
    {
        var environmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        return string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase);
    }

    public static DistributedLockProviderType ResolveProvider(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return DistributedLockProviderType.Valkey;
        }

        if (string.Equals(provider, "Valkey", StringComparison.OrdinalIgnoreCase))
        {
            return DistributedLockProviderType.Valkey;
        }

        throw new InvalidOperationException(
            $"Unsupported DistributedLocking:Provider value '{provider}'. Supported values: Valkey.");
    }

    public static void ValidateProvider(DistributedLockProviderType provider, string? valkeyConfiguration)
    {
        if (provider != DistributedLockProviderType.Valkey)
        {
            throw new InvalidOperationException("DistributedLocking:Provider must be Valkey.");
        }

        if (provider == DistributedLockProviderType.Valkey && string.IsNullOrWhiteSpace(valkeyConfiguration))
        {
            throw new InvalidOperationException(
                "DistributedLocking Valkey configuration is required. Configure DistributedLocking:Valkey:Configuration or Caching:Valkey:Configuration.");
        }
    }
}
