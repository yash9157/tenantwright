namespace AuditLogs.Health;

public sealed class AuditLogsOperationalHealthOptions
{
    public const string SectionName = "AuditLogging:OperationalHealth";

    public int TimeoutSeconds { get; set; } = 5;

    public int HangfireHeartbeatGraceSeconds { get; set; } = 120;

    public long MaximumEligibleSealLagRows { get; set; } = 10_000;

    public string BlobCanaryPrefix { get; set; } = "_health/";
}
