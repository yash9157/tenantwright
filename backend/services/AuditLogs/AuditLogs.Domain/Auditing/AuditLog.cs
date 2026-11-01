using Shared.AuditLogs.Auditing;
using Shared.Domain;
using Shared.Domain.Entities.Auditing;

namespace AuditLogs.Domain.Auditing;

public class AuditLog : CreationAuditedAggregateRoot<long>, IMultiTenant
{
    public Guid? TenantId { get; private set; }

    public Guid EventId { get; private set; }

    public string ApplicationName { get; private set; } = default!;

    public string Category { get; private set; } = default!;

    public string? UserId { get; private set; }

    public string? UserName { get; private set; }

    public string? TenantName { get; private set; }

    public string? ImpersonatorUserId { get; private set; }

    public string? ImpersonatorUserName { get; private set; }

    public Guid? ImpersonatorTenantId { get; private set; }

    public string? ImpersonatorTenantName { get; private set; }

    public DateTime ExecutionTime { get; private set; }

    public int ExecutionDuration { get; private set; }

    public string? ClientIpAddress { get; private set; }

    public string? ClientName { get; private set; }

    public string? ClientId { get; private set; }

    public string? CorrelationId { get; private set; }

    public string? TraceId { get; private set; }

    public string? BrowserInfo { get; private set; }

    public string? HttpMethod { get; private set; }

    public string? Url { get; private set; }

    public int? HttpStatusCode { get; private set; }

    public string? Exceptions { get; private set; }

    public string? Comments { get; private set; }

    /// <summary>
    /// Hex-encoded HMAC over this row's canonical fields, captured at ingest time from the producer's
    /// signed audit-log write contract. Null for rows written before sub-flow 1a, or when
    /// producer signing is not configured. Re-verified on read/export via
    /// <c>AuditLogEventSigner.VerifyPersisted</c> (sub-flow 1: at-rest tamper-evidence, modification detection).
    /// </summary>
    public string? Signature { get; private set; }

    public string? SignatureKeyId { get; private set; }

    public DateTime? SignatureTimestampUtc { get; private set; }

    public virtual ICollection<AuditLogAction> Actions { get; protected set; } = [];

    public virtual ICollection<EntityChange> EntityChanges { get; protected set; } = [];

    protected AuditLog()
    {
    }

    public AuditLog(
        long id,
        Guid eventId,
        string applicationName,
        string category,
        Guid? tenantId,
        string? tenantName,
        string? userId,
        string? userName,
        DateTime executionTime,
        int executionDuration,
        string? clientIpAddress,
        string? clientName,
        string? clientId,
        string? correlationId,
        string? traceId,
        string? browserInfo,
        string? httpMethod,
        string? url,
        int? httpStatusCode,
        string? impersonatorUserId,
        string? impersonatorUserName,
        Guid? impersonatorTenantId,
        string? impersonatorTenantName,
        string? exceptions,
        string? comments,
        IReadOnlyDictionary<string, string?>? extraProperties = null,
        string? signature = null,
        string? signatureKeyId = null,
        DateTime? signatureTimestampUtc = null)
        : base(id)
    {
        if (eventId == Guid.Empty)
        {
            throw new ArgumentException("eventId is required.", nameof(eventId));
        }

        EventId = eventId;
        TenantId = tenantId;
        ApplicationName = global::Shared.Check.NotNullOrWhiteSpace(applicationName, nameof(applicationName), AuditLogConsts.MaxApplicationNameLength);
        Category = global::Shared.Check.NotNullOrWhiteSpace(category, nameof(category), AuditLogConsts.MaxCategoryLength);
        TenantName = NormalizeOptional(tenantName, nameof(tenantName), AuditLogConsts.MaxTenantNameLength);
        UserId = NormalizeOptional(userId, nameof(userId), AuditLogConsts.MaxUserIdLength);
        UserName = NormalizeOptional(userName, nameof(userName), AuditLogConsts.MaxUserNameLength);
        ExecutionTime = NormalizeUtc(executionTime);
        ExecutionDuration = Math.Max(0, executionDuration);
        ClientIpAddress = NormalizeOptional(clientIpAddress, nameof(clientIpAddress), AuditLogConsts.MaxClientIpAddressLength);
        ClientName = NormalizeOptional(clientName, nameof(clientName), AuditLogConsts.MaxClientNameLength);
        ClientId = NormalizeOptional(clientId, nameof(clientId), AuditLogConsts.MaxClientIdLength);
        CorrelationId = NormalizeOptional(correlationId, nameof(correlationId), AuditLogConsts.MaxCorrelationIdLength);
        TraceId = NormalizeOptional(traceId, nameof(traceId), AuditLogConsts.MaxTraceIdLength);
        BrowserInfo = NormalizeOptional(browserInfo, nameof(browserInfo), AuditLogConsts.MaxBrowserInfoLength);
        HttpMethod = NormalizeOptional(httpMethod, nameof(httpMethod), AuditLogConsts.MaxHttpMethodLength);
        Url = NormalizeOptional(url, nameof(url), AuditLogConsts.MaxUrlLength);
        HttpStatusCode = httpStatusCode;
        ImpersonatorUserId = NormalizeOptional(impersonatorUserId, nameof(impersonatorUserId), AuditLogConsts.MaxUserIdLength);
        ImpersonatorUserName = NormalizeOptional(impersonatorUserName, nameof(impersonatorUserName), AuditLogConsts.MaxUserNameLength);
        ImpersonatorTenantId = impersonatorTenantId;
        ImpersonatorTenantName = NormalizeOptional(impersonatorTenantName, nameof(impersonatorTenantName), AuditLogConsts.MaxTenantNameLength);
        Exceptions = NormalizeOptional(exceptions, nameof(exceptions), AuditLogConsts.MaxExceptionPayloadLength);
        Comments = NormalizeOptional(comments, nameof(comments), AuditLogConsts.MaxCommentsLength);
        Signature = NormalizeOptional(signature, nameof(signature), AuditLogConsts.MaxSignatureLength);
        SignatureKeyId = NormalizeOptional(signatureKeyId, nameof(signatureKeyId), AuditLogConsts.MaxSignatureKeyIdLength);
        SignatureTimestampUtc = signatureTimestampUtc.HasValue ? NormalizeUtc(signatureTimestampUtc.Value) : null;
        ApplyExtraProperties(extraProperties);
    }

    private static DateTime NormalizeUtc(DateTime value)
    {
        if (value == default)
        {
            return DateTime.UtcNow;
        }

        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }

    private static string? NormalizeOptional(string? value, string paramName, int maxLength)
    {
        var normalized = value?.Trim();
        if (normalized is { Length: > 0 } && normalized.Length > maxLength)
        {
            throw new ArgumentException($"{paramName} length must be equal to or lower than {maxLength}.", paramName);
        }

        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private void ApplyExtraProperties(IReadOnlyDictionary<string, string?>? extraProperties)
    {
        if (extraProperties == null || extraProperties.Count == 0)
        {
            return;
        }

        foreach (var pair in extraProperties)
        {
            var key = NormalizeOptional(pair.Key, nameof(extraProperties), AuditLogConsts.MaxExtraPropertyKeyLength);
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            ExtraProperties[key] = NormalizeOptional(
                pair.Value,
                $"{nameof(extraProperties)}[{key}]",
                AuditLogConsts.MaxExtraPropertyValueLength);
        }
    }
}
