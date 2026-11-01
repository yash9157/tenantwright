using System.Security.Cryptography;
using System.Text;
using Shared.Data.ConnectionStrings;
using Tenant.Application.Contracts.Tenants;

namespace Tenant.Application.Tenants;

/// <summary>
/// Secret-free projection of a tenant's dedicated database assignments for every dedicatable catalog name.
/// </summary>
internal static class TenantDatabaseStatusMapper
{
    private const int DisplayFingerprintLength = 12;

    public static IReadOnlyList<TenantDatabaseStatusDto> Map(Domain.Tenants.Tenant tenant) =>
        TenantWrightDatabaseCatalog.TenantDedicatable.Select(name => Map(tenant, name)).ToList();

    public static TenantDatabaseStatusDto Map(Domain.Tenants.Tenant tenant, string name)
    {
        var storedValue = tenant.FindConnectionString(name);
        if (string.IsNullOrWhiteSpace(storedValue))
        {
            return new TenantDatabaseStatusDto { Name = name, IsDedicated = false };
        }

        return new TenantDatabaseStatusDto
        {
            Name = name,
            IsDedicated = true,
            Fingerprint = DisplayFingerprint(tenant.FindConnectionTargetFingerprint(name), storedValue)
        };
    }

    public static int CountDedicated(Domain.Tenants.Tenant tenant) =>
        TenantWrightDatabaseCatalog.TenantDedicatable.Count(name =>
            !string.IsNullOrWhiteSpace(tenant.FindConnectionString(name)));

    /// <summary>
    /// The short prefix of the stored target fingerprint, so two assignments to the same physical database show the
    /// same value and a credential rotation does not change it. Rows written before target fingerprints existed fall
    /// back to a hash of the protected value; neither form is reversible to the connection string.
    /// </summary>
    private static string DisplayFingerprint(string? targetFingerprint, string storedValue)
    {
        if (!string.IsNullOrWhiteSpace(targetFingerprint))
        {
            return targetFingerprint[..Math.Min(DisplayFingerprintLength, targetFingerprint.Length)];
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(storedValue));
        return Convert.ToHexString(hash).ToLowerInvariant()[..DisplayFingerprintLength];
    }
}
