using Shared.BackgroundTasks.Jobs;

namespace AuditLogs.Application.Auditing;

[BackgroundJobName("auditlogs.export")]
public sealed record AuditLogExportJobArgs(long ExportId, Guid? TenantId);

[BackgroundJobName("auditlogs.archive-maintenance")]
public sealed record AuditLogArchiveMaintenanceJobArgs(long? RestoreJobId = null);

[BackgroundJobName("auditlogs.seal-verification")]
public sealed record AuditLogSealVerificationJobArgs(long VerificationRunId);
