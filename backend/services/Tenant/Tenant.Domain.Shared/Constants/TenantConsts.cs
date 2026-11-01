namespace Tenant.Domain.Shared.Constants;

public static class TenantConsts
{
    public const int MaxNameLength = 64;
    public const int MaxNormalizedNameLength = 64;
    public const int MaxConnectionStringNameLength = 64;

    /// <summary>Lower-case hex SHA-256 of the normalized (host, port, database) target.</summary>
    public const int MaxConnectionTargetFingerprintLength = 64;
}
