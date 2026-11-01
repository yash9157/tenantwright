namespace AuditLogs.Application.Auditing;

internal sealed class AuditLogExportFilterSnapshot
{
    public Guid? TenantId { get; set; }

    public DateTime? FromUtc { get; set; }

    public DateTime? ToUtc { get; set; }

    public string? UserId { get; set; }

    public string? ApplicationName { get; set; }

    public string? Category { get; set; }

    public string? UrlContains { get; set; }

    public int? HttpStatusCode { get; set; }

    public string? ClientIpAddress { get; set; }

    public string? CorrelationId { get; set; }

    public string? TraceId { get; set; }

    public int MaxRowCount { get; set; }
}
