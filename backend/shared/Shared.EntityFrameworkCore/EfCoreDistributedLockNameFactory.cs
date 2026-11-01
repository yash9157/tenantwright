using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace Shared.EntityFrameworkCore;

internal static class EfCoreDistributedLockNameFactory
{
    public static string BuildMigrationLockName(Type dbContextType, DbContext dbContext)
    {
        return $"efcore:migrate:{dbContextType.FullName}:{BuildConnectionFingerprint(dbContext)}";
    }

    /// <summary>
    /// Serializes migrations of one tenant's dedicated database for one DbContext across replicas, triggers
    /// (startup, migrate-and-exit, <c>TenantDatabaseChangedEto</c>) and redeliveries.
    /// </summary>
    public static string BuildTenantDatabaseMigrationLockName(Type dbContextType, Guid tenantId)
    {
        return $"efcore:migrate:{dbContextType.FullName}:tenant:{tenantId:N}";
    }

    private static string BuildConnectionFingerprint(DbContext dbContext)
    {
        var connection = dbContext.Database.GetDbConnection();
        var providerName = dbContext.Database.ProviderName ?? string.Empty;
        var dataSource = connection.DataSource ?? string.Empty;
        var database = connection.Database ?? string.Empty;
        var input = $"{providerName}|{dataSource}|{database}";

        if (string.IsNullOrWhiteSpace(input.Replace("|", string.Empty, StringComparison.Ordinal)))
        {
            input = dbContext.Database.GetConnectionString() ?? dbContext.GetType().FullName ?? "unknown";
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes[..8]).ToLowerInvariant();
    }
}
