namespace Shared.Caching;

internal static class CachingEnvironmentGuard
{
    public static bool IsDevelopment()
    {
        var environmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        return string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase);
    }

    public static void ValidateProvider(
        string? provider,
        string? valkeyConfiguration,
        string? valkeyPassword,
        string? trustedCaCertificatePath)
    {
        var isDevelopment = IsDevelopment();
        var isValkey = string.Equals(provider, "Valkey", StringComparison.OrdinalIgnoreCase);

        if (!isDevelopment && !isValkey)
        {
            throw new InvalidOperationException("Caching:Provider must be Valkey outside Development.");
        }

        if (!isDevelopment && string.IsNullOrWhiteSpace(valkeyConfiguration))
        {
            throw new InvalidOperationException("Caching:Valkey:Configuration is required outside Development.");
        }

        if (!isDevelopment && string.IsNullOrWhiteSpace(valkeyPassword))
        {
            throw new InvalidOperationException("Caching:Valkey:Password is required outside Development.");
        }

        if (!isDevelopment && string.IsNullOrWhiteSpace(trustedCaCertificatePath))
        {
            throw new InvalidOperationException(
                "Caching:Valkey:TrustedCaCertificatePath is required outside Development.");
        }
    }
}
