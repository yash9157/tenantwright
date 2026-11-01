namespace AuditLogs.Application.Auditing;

public sealed class AuditLogSealOptions
{
    public const string SectionName = "AuditLogging:Sealing";

    public bool Enabled { get; set; }

    /// <summary>
    /// Rows younger than this many seconds are never sealed yet, even if their Id is otherwise
    /// eligible. This is the safety watermark: concurrent transactions can commit a row with a lower
    /// Id after a row with a higher Id has already committed (e.g. two overlapping INSERTs racing on
    /// the identity sequence), so sealing right up to <c>max(Id)</c> could seal past a row that hasn't
    /// committed yet and never come back for it, permanently breaking future chain verification.
    /// Waiting this long after "now" before considering a row's Id "final" makes that race practically
    /// impossible under normal load.
    /// </summary>
    public int WatermarkLagSeconds { get; set; } = 120;

    /// <summary>
    /// Maximum number of AuditLog rows hashed into a single seal per run.
    /// </summary>
    public int MaxBatchSize { get; set; } = 5000;

    public int IntervalSeconds { get; set; } = 60;
}

public static class AuditLogSealOptionsPostConfigure
{
    public static void Normalize(AuditLogSealOptions options)
    {
        options ??= new AuditLogSealOptions();
        options.WatermarkLagSeconds = Math.Clamp(options.WatermarkLagSeconds, 30, 3600);
        options.MaxBatchSize = Math.Clamp(options.MaxBatchSize, 100, 50000);
        options.IntervalSeconds = Math.Clamp(options.IntervalSeconds, 10, 3600);
    }
}
