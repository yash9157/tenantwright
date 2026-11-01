namespace AuditLogs.Domain.Shared.Auditing;

/// <summary>
/// Stable caller-safe error codes for AuditLogs public operations.
/// </summary>
public static class AuditLogsErrorCodes
{
    public const string AuditLogNotFound = "audit_log_not_found";
    public const string AuditEntityChangeNotFound = "audit_entity_change_not_found";
    public const string AuditExportNotFound = "audit_export_not_found";
    public const string InvalidDateRange = "audit_invalid_date_range";
    public const string ExportWindowExceeded = "audit_export_window_exceeded";
    public const string InvalidFieldLength = "audit_invalid_field_length";
    public const string InvalidExportFormat = "audit_invalid_export_format";
    public const string InvalidExportStatusFilter = "audit_invalid_export_status_filter";
    public const string EntityTypeRequired = "audit_entity_type_required";
    public const string EntityIdRequired = "audit_entity_id_required";
    public const string TenantIdRequired = "audit_tenant_id_required";
    public const string DownloadTokenRequired = "audit_download_token_required";
    public const string ExportExpired = "audit_export_expired";
    public const string ExportNotReady = "audit_export_not_ready";
    public const string SealRangeInvalid = "audit_seal_range_invalid";
    public const string SealRangeExceeded = "audit_seal_range_exceeded";
    public const string SealVerificationNotFound = "audit_seal_verification_not_found";
    public const string ArchiveRestoreDisabled = "audit_archive_restore_disabled";
    public const string ArchiveManifestNotFound = "audit_archive_manifest_not_found";
    public const string ArchiveRestoreJobNotFound = "audit_archive_restore_job_not_found";
    public const string ArchiveRestoreStatusInvalid = "audit_archive_restore_status_invalid";
    public const string SealVerificationStatusInvalid = "audit_seal_verification_status_invalid";
    public const string ArchiveStatusInvalid = "audit_archive_status_invalid";
    public const string DashboardWindowExceeded = "audit_dashboard_window_exceeded";
}
