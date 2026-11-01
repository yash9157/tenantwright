using System.Diagnostics.Metrics;

namespace AuditLogs.Health;

internal static class AuditLogsOperationalHealthTelemetry
{
    public const string MeterName = "AuditLogs.OperationalHealth";
    private static readonly Meter Meter = new(MeterName, "1.0.0");
    private static long _hangfireHealthy = 1;
    private static long _sealLagRows;
    private static long _archiveBlobHealthy = 1;

    static AuditLogsOperationalHealthTelemetry()
    {
        Meter.CreateObservableGauge("auditlogs.hangfire.server.healthy", () => Interlocked.Read(ref _hangfireHealthy));
        Meter.CreateObservableGauge("auditlogs.seal.lag.rows", () => Interlocked.Read(ref _sealLagRows));
        Meter.CreateObservableGauge("auditlogs.archive.blob.healthy", () => Interlocked.Read(ref _archiveBlobHealthy));
    }

    public static void RecordHangfire(bool healthy) => Interlocked.Exchange(ref _hangfireHealthy, healthy ? 1 : 0);

    public static void RecordSealLag(long lag) => Interlocked.Exchange(ref _sealLagRows, Math.Max(0, lag));

    public static void RecordArchiveBlob(bool healthy) => Interlocked.Exchange(ref _archiveBlobHealthy, healthy ? 1 : 0);
}
