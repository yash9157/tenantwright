namespace AuditLogs.Domain.Shared.Auditing;

public sealed class AuditLogFilter
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
}

public sealed class EntityChangeFilter
{
    public Guid? TenantId { get; set; }

    public long? AuditLogId { get; set; }

    public DateTime? FromUtc { get; set; }

    public DateTime? ToUtc { get; set; }

    public string? EntityTypeFullName { get; set; }

    public string? EntityId { get; set; }
}
