using Shared.Domain;
using Shared.Events;

namespace Shared.EventContracts;

[EventName("Shared.EventContracts.AuditLogWriteRequestedTenantEto")]
[DistributedContract(1, DistributedContractScope.TenantRequired, DistributedContractDataClassification.Sensitive)]
public sealed class AuditLogWriteRequestedTenantEto : IMultiTenant
{
    public Guid? TenantId { get; set; }
    public Guid EventId { get; set; }
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
    public string SourceService { get; set; } = string.Empty;
    public string Category { get; set; } = "http";
    public string? UserId { get; set; }
    public string? UserName { get; set; }
    public string? TenantName { get; set; }
    public string? ImpersonatorUserId { get; set; }
    public Guid? ImpersonatorTenantId { get; set; }
    public string? ImpersonatorUserName { get; set; }
    public string? ImpersonatorTenantName { get; set; }
    public string? ClientIpAddress { get; set; }
    public string? ClientId { get; set; }
    public string? ClientName { get; set; }
    public string? CorrelationId { get; set; }
    public string? TraceId { get; set; }
    public string? HttpMethod { get; set; }
    public string? Path { get; set; }
    public string? UserAgent { get; set; }
    public int? StatusCode { get; set; }
    public double? DurationMs { get; set; }
    public Dictionary<string, string?> ExtraProperties { get; set; } = new(StringComparer.Ordinal);
    public List<string> Comments { get; set; } = [];
    public List<AuditLogExceptionEto> Exceptions { get; set; } = [];
    public List<AuditLogActionEto> Actions { get; set; } = [];
    public List<AuditLogEntityChangeEto> EntityChanges { get; set; } = [];
    public bool IsTruncated { get; set; }
    public int PayloadBytes { get; set; }
    public bool RedactionApplied { get; set; }
    public int RedactionVersion { get; set; }
    public string? SignatureKeyId { get; set; }
    public DateTime? SignatureTimestampUtc { get; set; }
    public string? Signature { get; set; }
}

[EventName("Shared.EventContracts.AuditLogWriteRequestedHostEto")]
[DistributedContract(1, DistributedContractScope.HostOnly, DistributedContractDataClassification.Sensitive)]
public sealed class AuditLogWriteRequestedHostEto
{
    public Guid EventId { get; set; }
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
    public string SourceService { get; set; } = string.Empty;
    public string Category { get; set; } = "http";
    public string? UserId { get; set; }
    public string? UserName { get; set; }
    public string? TenantName { get; set; }
    public string? ImpersonatorUserId { get; set; }
    public Guid? ImpersonatorTenantId { get; set; }
    public string? ImpersonatorUserName { get; set; }
    public string? ImpersonatorTenantName { get; set; }
    public string? ClientIpAddress { get; set; }
    public string? ClientId { get; set; }
    public string? ClientName { get; set; }
    public string? CorrelationId { get; set; }
    public string? TraceId { get; set; }
    public string? HttpMethod { get; set; }
    public string? Path { get; set; }
    public string? UserAgent { get; set; }
    public int? StatusCode { get; set; }
    public double? DurationMs { get; set; }
    public Dictionary<string, string?> ExtraProperties { get; set; } = new(StringComparer.Ordinal);
    public List<string> Comments { get; set; } = [];
    public List<AuditLogExceptionEto> Exceptions { get; set; } = [];
    public List<AuditLogActionEto> Actions { get; set; } = [];
    public List<AuditLogEntityChangeEto> EntityChanges { get; set; } = [];
    public bool IsTruncated { get; set; }
    public int PayloadBytes { get; set; }
    public bool RedactionApplied { get; set; }
    public int RedactionVersion { get; set; }
    public string? SignatureKeyId { get; set; }
    public DateTime? SignatureTimestampUtc { get; set; }
    public string? Signature { get; set; }
}

public sealed class AuditLogExceptionEto
{
    public string Type { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? StackTrace { get; set; }
}

public sealed class AuditLogActionEto
{
    public string Name { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? Result { get; set; }
    public double? DurationMs { get; set; }
    public Dictionary<string, string?> Parameters { get; set; } = new(StringComparer.Ordinal);
}

public sealed class AuditLogEntityChangeEto
{
    public string EntityType { get; set; } = string.Empty;
    public string? EntityId { get; set; }
    public string ChangeType { get; set; } = string.Empty;
    public Guid? TenantId { get; set; }
    public string? UserId { get; set; }
    public DateTime? ChangeTimeUtc { get; set; }
    public List<AuditLogEntityPropertyChangeEto> PropertyChanges { get; set; } = [];
}

public sealed class AuditLogEntityPropertyChangeEto
{
    public string PropertyName { get; set; } = string.Empty;
    public string? PropertyType { get; set; }
    public string? OriginalValue { get; set; }
    public string? NewValue { get; set; }
}
