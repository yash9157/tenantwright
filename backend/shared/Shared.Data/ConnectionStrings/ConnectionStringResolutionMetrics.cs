using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;

namespace Shared.Data.ConnectionStrings;

public static class ConnectionStringResolutionMetrics
{
    private static readonly Meter Meter = new("Shared.Data.ConnectionStrings");
    private static readonly Counter<long> ResolutionFailureCounter =
        Meter.CreateCounter<long>("shared.connection_strings.resolve_failures_total");
    private static readonly Counter<long> TenantFailClosedCounter =
        Meter.CreateCounter<long>("shared.connection_strings.tenant_fail_closed_total");
    private static readonly Counter<long> TenantHostFallbackCounter =
        Meter.CreateCounter<long>("shared.connection_strings.tenant_host_fallback_total");

    public static void RecordFailure(string resolverName, string? connectionStringName)
    {
        ResolutionFailureCounter.Add(
            1,
            new KeyValuePair<string, object?>("resolver", resolverName),
            new KeyValuePair<string, object?>("name_hash", HashConnectionStringName(connectionStringName)));
    }

    public static void RecordTenantFailClosed(string? connectionStringName)
    {
        TenantFailClosedCounter.Add(
            1,
            new KeyValuePair<string, object?>("name_hash", HashConnectionStringName(connectionStringName)));
    }

    public static void RecordTenantHostFallback(string? connectionStringName)
    {
        TenantHostFallbackCounter.Add(
            1,
            new KeyValuePair<string, object?>("name_hash", HashConnectionStringName(connectionStringName)));
    }

    private static string HashConnectionStringName(string? connectionStringName)
    {
        if (string.IsNullOrWhiteSpace(connectionStringName))
        {
            return "default";
        }

        var bytes = Encoding.UTF8.GetBytes(connectionStringName.Trim());
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
