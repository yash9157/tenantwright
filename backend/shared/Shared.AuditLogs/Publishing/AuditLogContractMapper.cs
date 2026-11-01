using Shared.EventContracts;

namespace Shared.AuditLogs.Publishing;

public static class AuditLogContractMapper
{
    public static AuditLogWriteRequestedTenantEto ToTenant(AuditLogWriteRequest source)
    {
        var tenantId = source.TenantId;
        if (!tenantId.HasValue || tenantId.Value == Guid.Empty)
        {
            throw new InvalidOperationException("Tenant audit log commands require a non-empty tenant id.");
        }

        return new AuditLogWriteRequestedTenantEto
        {
            TenantId = tenantId.Value,
            EventId = source.EventId,
            OccurredAtUtc = source.OccurredAtUtc,
            SourceService = source.SourceService ?? string.Empty,
            Category = source.Category,
            UserId = source.UserId,
            UserName = source.UserName,
            TenantName = source.TenantName,
            ImpersonatorUserId = source.ImpersonatorUserId,
            ImpersonatorTenantId = source.ImpersonatorTenantId,
            ImpersonatorUserName = source.ImpersonatorUserName,
            ImpersonatorTenantName = source.ImpersonatorTenantName,
            ClientIpAddress = source.ClientIpAddress,
            ClientId = source.ClientId,
            ClientName = source.ClientName,
            CorrelationId = source.CorrelationId,
            TraceId = source.TraceId,
            HttpMethod = source.HttpMethod,
            Path = source.Path,
            UserAgent = source.UserAgent,
            StatusCode = source.StatusCode,
            DurationMs = source.DurationMs,
            ExtraProperties = new Dictionary<string, string?>(source.ExtraProperties, StringComparer.Ordinal),
            Comments = source.Comments.ToList(),
            Exceptions = MapExceptions(source.Exceptions),
            Actions = MapActions(source.Actions),
            EntityChanges = MapEntityChanges(source.EntityChanges),
            IsTruncated = source.IsTruncated,
            PayloadBytes = source.PayloadBytes,
            RedactionApplied = source.RedactionApplied,
            RedactionVersion = source.RedactionVersion,
            SignatureKeyId = source.SignatureKeyId,
            SignatureTimestampUtc = source.SignatureTimestampUtc,
            Signature = source.Signature
        };
    }

    public static AuditLogWriteRequestedHostEto ToHost(AuditLogWriteRequest source)
    {
        if (source.TenantId.HasValue && source.TenantId.Value != Guid.Empty)
        {
            throw new InvalidOperationException("Host audit log commands cannot carry a tenant id.");
        }

        return new AuditLogWriteRequestedHostEto
        {
            EventId = source.EventId,
            OccurredAtUtc = source.OccurredAtUtc,
            SourceService = source.SourceService ?? string.Empty,
            Category = source.Category,
            UserId = source.UserId,
            UserName = source.UserName,
            TenantName = source.TenantName,
            ImpersonatorUserId = source.ImpersonatorUserId,
            ImpersonatorTenantId = source.ImpersonatorTenantId,
            ImpersonatorUserName = source.ImpersonatorUserName,
            ImpersonatorTenantName = source.ImpersonatorTenantName,
            ClientIpAddress = source.ClientIpAddress,
            ClientId = source.ClientId,
            ClientName = source.ClientName,
            CorrelationId = source.CorrelationId,
            TraceId = source.TraceId,
            HttpMethod = source.HttpMethod,
            Path = source.Path,
            UserAgent = source.UserAgent,
            StatusCode = source.StatusCode,
            DurationMs = source.DurationMs,
            ExtraProperties = new Dictionary<string, string?>(source.ExtraProperties, StringComparer.Ordinal),
            Comments = source.Comments.ToList(),
            Exceptions = MapExceptions(source.Exceptions),
            Actions = MapActions(source.Actions),
            EntityChanges = MapEntityChanges(source.EntityChanges),
            IsTruncated = source.IsTruncated,
            PayloadBytes = source.PayloadBytes,
            RedactionApplied = source.RedactionApplied,
            RedactionVersion = source.RedactionVersion,
            SignatureKeyId = source.SignatureKeyId,
            SignatureTimestampUtc = source.SignatureTimestampUtc,
            Signature = source.Signature
        };
    }

    public static AuditLogWriteRequest ToRequest(AuditLogWriteRequestedTenantEto source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.TenantId.HasValue || source.TenantId.Value == Guid.Empty)
        {
            throw new InvalidOperationException("Tenant audit log commands require a non-empty tenant id.");
        }

        return CreateRequest(
            source.TenantId.Value,
            source.EventId,
            source.OccurredAtUtc,
            source.SourceService,
            source.Category,
            source.UserId,
            source.UserName,
            source.TenantName,
            source.ImpersonatorUserId,
            source.ImpersonatorTenantId,
            source.ImpersonatorUserName,
            source.ImpersonatorTenantName,
            source.ClientIpAddress,
            source.ClientId,
            source.ClientName,
            source.CorrelationId,
            source.TraceId,
            source.HttpMethod,
            source.Path,
            source.UserAgent,
            source.StatusCode,
            source.DurationMs,
            source.ExtraProperties,
            source.Comments,
            source.Exceptions,
            source.Actions,
            source.EntityChanges,
            source.IsTruncated,
            source.PayloadBytes,
            source.RedactionApplied,
            source.RedactionVersion,
            source.SignatureKeyId,
            source.SignatureTimestampUtc,
            source.Signature);
    }

    public static AuditLogWriteRequest ToRequest(AuditLogWriteRequestedHostEto source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return CreateRequest(
            tenantId: null,
            source.EventId,
            source.OccurredAtUtc,
            source.SourceService,
            source.Category,
            source.UserId,
            source.UserName,
            source.TenantName,
            source.ImpersonatorUserId,
            source.ImpersonatorTenantId,
            source.ImpersonatorUserName,
            source.ImpersonatorTenantName,
            source.ClientIpAddress,
            source.ClientId,
            source.ClientName,
            source.CorrelationId,
            source.TraceId,
            source.HttpMethod,
            source.Path,
            source.UserAgent,
            source.StatusCode,
            source.DurationMs,
            source.ExtraProperties,
            source.Comments,
            source.Exceptions,
            source.Actions,
            source.EntityChanges,
            source.IsTruncated,
            source.PayloadBytes,
            source.RedactionApplied,
            source.RedactionVersion,
            source.SignatureKeyId,
            source.SignatureTimestampUtc,
            source.Signature);
    }

    private static AuditLogWriteRequest CreateRequest(
        Guid? tenantId,
        Guid eventId,
        DateTime occurredAtUtc,
        string sourceService,
        string category,
        string? userId,
        string? userName,
        string? tenantName,
        string? impersonatorUserId,
        Guid? impersonatorTenantId,
        string? impersonatorUserName,
        string? impersonatorTenantName,
        string? clientIpAddress,
        string? clientId,
        string? clientName,
        string? correlationId,
        string? traceId,
        string? httpMethod,
        string? path,
        string? userAgent,
        int? statusCode,
        double? durationMs,
        IReadOnlyDictionary<string, string?> extraProperties,
        IReadOnlyCollection<string> comments,
        IReadOnlyCollection<AuditLogExceptionEto> exceptions,
        IReadOnlyCollection<AuditLogActionEto> actions,
        IReadOnlyCollection<AuditLogEntityChangeEto> entityChanges,
        bool isTruncated,
        int payloadBytes,
        bool redactionApplied,
        int redactionVersion,
        string? signatureKeyId,
        DateTime? signatureTimestampUtc,
        string? signature)
    {
        return new AuditLogWriteRequest
        {
            TenantId = tenantId,
            EventId = eventId,
            OccurredAtUtc = occurredAtUtc,
            SourceService = sourceService,
            Category = category,
            UserId = userId,
            UserName = userName,
            TenantName = tenantName,
            ImpersonatorUserId = impersonatorUserId,
            ImpersonatorTenantId = impersonatorTenantId,
            ImpersonatorUserName = impersonatorUserName,
            ImpersonatorTenantName = impersonatorTenantName,
            ClientIpAddress = clientIpAddress,
            ClientId = clientId,
            ClientName = clientName,
            CorrelationId = correlationId,
            TraceId = traceId,
            HttpMethod = httpMethod,
            Path = path,
            UserAgent = userAgent,
            StatusCode = statusCode,
            DurationMs = durationMs,
            ExtraProperties = new Dictionary<string, string?>(extraProperties, StringComparer.Ordinal),
            Comments = comments.ToList(),
            Exceptions = exceptions.Select(exception => new AuditLogWriteRequestException
            {
                Type = exception.Type,
                Message = exception.Message,
                StackTrace = exception.StackTrace
            }).ToList(),
            Actions = actions.Select(action => new AuditLogWriteRequestAction
            {
                Name = action.Name,
                DisplayName = action.DisplayName,
                Result = action.Result,
                DurationMs = action.DurationMs,
                Parameters = new Dictionary<string, string?>(action.Parameters, StringComparer.Ordinal)
            }).ToList(),
            EntityChanges = entityChanges.Select(change => new AuditLogWriteRequestEntityChange
            {
                EntityType = change.EntityType,
                EntityId = change.EntityId,
                ChangeType = change.ChangeType,
                TenantId = change.TenantId,
                UserId = change.UserId,
                ChangeTimeUtc = change.ChangeTimeUtc,
                PropertyChanges = change.PropertyChanges.Select(property => new AuditLogWriteRequestPropertyChange
                {
                    PropertyName = property.PropertyName,
                    PropertyType = property.PropertyType,
                    OriginalValue = property.OriginalValue,
                    NewValue = property.NewValue
                }).ToList()
            }).ToList(),
            IsTruncated = isTruncated,
            PayloadBytes = payloadBytes,
            RedactionApplied = redactionApplied,
            RedactionVersion = redactionVersion,
            SignatureKeyId = signatureKeyId,
            SignatureTimestampUtc = signatureTimestampUtc,
            Signature = signature
        };
    }

    private static List<AuditLogExceptionEto> MapExceptions(
        IEnumerable<AuditLogWriteRequestException> exceptions)
        => exceptions.Select(exception => new AuditLogExceptionEto
        {
            Type = exception.Type,
            Message = exception.Message,
            StackTrace = exception.StackTrace
        }).ToList();

    private static List<AuditLogActionEto> MapActions(
        IEnumerable<AuditLogWriteRequestAction> actions)
        => actions.Select(action => new AuditLogActionEto
        {
            Name = action.Name,
            DisplayName = action.DisplayName,
            Result = action.Result,
            DurationMs = action.DurationMs,
            Parameters = new Dictionary<string, string?>(action.Parameters, StringComparer.Ordinal)
        }).ToList();

    private static List<AuditLogEntityChangeEto> MapEntityChanges(
        IEnumerable<AuditLogWriteRequestEntityChange> entityChanges)
        => entityChanges.Select(change => new AuditLogEntityChangeEto
        {
            EntityType = change.EntityType,
            EntityId = change.EntityId,
            ChangeType = change.ChangeType,
            TenantId = change.TenantId,
            UserId = change.UserId,
            ChangeTimeUtc = change.ChangeTimeUtc,
            PropertyChanges = change.PropertyChanges.Select(property => new AuditLogEntityPropertyChangeEto
            {
                PropertyName = property.PropertyName,
                PropertyType = property.PropertyType,
                OriginalValue = property.OriginalValue,
                NewValue = property.NewValue
            }).ToList()
        }).ToList();
}
