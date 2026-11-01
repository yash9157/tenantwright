namespace AuditLogs.Domain.Shared.Auditing;

public static class AuditLogExtraPropertyNames
{
    public const string PayloadBytes = "audit.payload_bytes";
    public const string IsTruncated = "audit.is_truncated";
    public const string RedactionApplied = "audit.redaction_applied";
    public const string RedactionVersion = "audit.redaction_version";
}
