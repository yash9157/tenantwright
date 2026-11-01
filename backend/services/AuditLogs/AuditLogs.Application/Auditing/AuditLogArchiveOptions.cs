namespace AuditLogs.Application.Auditing;

public sealed class AuditLogArchiveOptions
{
    public const string SectionName = "AuditLogging:Archive";

    public bool Enabled { get; set; }

    public int HotRetentionDays { get; set; } = 365;

    public int BlobRetentionYears { get; set; } = 10;

    public string PartitionUnit { get; set; } = "Month";

    public int MaxPartitionsPerCycle { get; set; } = 3;

    public int RowBatchSize { get; set; } = 500;

    public int DeleteBatchSize { get; set; } = 2000;

    public string RecurringCron { get; set; } = "0 * * * *";

    public bool DeleteArchivedFromHotStore { get; set; }

    public bool AllowRestore { get; set; } = true;

    public int MaxRestoreRowsPerRun { get; set; } = 50000;

    public int MaxConcurrentRestorePartitions { get; set; } = 1;

    public int InteractiveSealVerificationMaxRows { get; set; } = 50000;

    public int MaxSealVerificationRows { get; set; } = 5000000;

    public AuditLogArchiveBlobOptions Blob { get; set; } = new();
}

public sealed class AuditLogArchiveBlobOptions
{
    public string Provider { get; set; } = "FileSystem";

    public string? ContainerName { get; set; } = "auditlogs-archive";

    public bool CreateContainerIfNotExists { get; set; } = true;

    public AuditLogArchiveBlobFileSystemOptions FileSystem { get; set; } = new();

    public AuditLogArchiveBlobAzureOptions Azure { get; set; } = new();

    public AuditLogArchiveBlobAwsOptions Aws { get; set; } = new();
}

public sealed class AuditLogArchiveBlobFileSystemOptions
{
    public string? BasePath { get; set; }

    public bool AppendContainerNameToBasePath { get; set; } = true;

    public int RetryCount { get; set; } = 3;
}

public sealed class AuditLogArchiveBlobAzureOptions
{
    public string? ConnectionString { get; set; }

    public string? ContainerName { get; set; }
}

public sealed class AuditLogArchiveBlobAwsOptions
{
    public string? Region { get; set; }

    public string? AccessKeyId { get; set; }

    public string? SecretAccessKey { get; set; }

    public string? SessionToken { get; set; }

    public string? ContainerName { get; set; }
}

public static class AuditLogArchiveOptionsPostConfigure
{
    public static void Normalize(AuditLogArchiveOptions options)
    {
        options ??= new AuditLogArchiveOptions();

        options.HotRetentionDays = Math.Clamp(options.HotRetentionDays, 1, 3650);
        options.BlobRetentionYears = Math.Clamp(options.BlobRetentionYears, 1, 25);
        options.MaxPartitionsPerCycle = Math.Clamp(options.MaxPartitionsPerCycle, 1, 50);
        options.RowBatchSize = Math.Clamp(options.RowBatchSize, 50, 5000);
        options.DeleteBatchSize = Math.Clamp(options.DeleteBatchSize, 100, 10000);
        options.RecurringCron = string.IsNullOrWhiteSpace(options.RecurringCron)
            ? "0 * * * *"
            : options.RecurringCron.Trim();
        options.MaxRestoreRowsPerRun = Math.Clamp(options.MaxRestoreRowsPerRun, 100, 500000);
        options.MaxConcurrentRestorePartitions = Math.Clamp(options.MaxConcurrentRestorePartitions, 1, 5);
        options.InteractiveSealVerificationMaxRows = Math.Clamp(
            options.InteractiveSealVerificationMaxRows,
            100,
            500000);
        options.MaxSealVerificationRows = Math.Clamp(
            options.MaxSealVerificationRows,
            options.InteractiveSealVerificationMaxRows,
            10000000);

        options.PartitionUnit = string.Equals(options.PartitionUnit, "Month", StringComparison.OrdinalIgnoreCase)
            ? "Month"
            : "Month";

        options.Blob ??= new AuditLogArchiveBlobOptions();
        options.Blob.Provider = string.IsNullOrWhiteSpace(options.Blob.Provider)
            ? "FileSystem"
            : options.Blob.Provider.Trim();
        options.Blob.ContainerName = string.IsNullOrWhiteSpace(options.Blob.ContainerName)
            ? "auditlogs-archive"
            : options.Blob.ContainerName.Trim();

        options.Blob.FileSystem ??= new AuditLogArchiveBlobFileSystemOptions();
        if (options.Blob.FileSystem.RetryCount < 1)
        {
            options.Blob.FileSystem.RetryCount = 1;
        }

        options.Blob.Azure ??= new AuditLogArchiveBlobAzureOptions();
        options.Blob.Aws ??= new AuditLogArchiveBlobAwsOptions();
    }
}
