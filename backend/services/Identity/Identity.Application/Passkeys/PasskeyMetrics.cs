using System.Diagnostics.Metrics;

namespace Identity.Application.Passkeys;

internal static class PasskeyMetrics
{
    private static readonly Meter Meter = new("TenantWright.Identity.Passkeys", "1.0.0");
    private static readonly Counter<long> Operations = Meter.CreateCounter<long>(
        "tenantwright.identity.passkey.operations",
        description: "Passkey operations by low-cardinality operation and outcome.");

    public static void Record(string operation, string outcome)
    {
        Operations.Add(1,
            new KeyValuePair<string, object?>("operation", operation),
            new KeyValuePair<string, object?>("outcome", outcome));
    }
}
