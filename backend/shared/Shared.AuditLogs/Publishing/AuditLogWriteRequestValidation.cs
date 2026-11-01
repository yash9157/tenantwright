using System.Text.Json;
using Shared.EventContracts;

namespace Shared.AuditLogs.Publishing;

public static class AuditLogWriteRequestValidation
{
    public const int MaxSourceServiceLength = 64;
    public const int MaxCategoryLength = 64;
    public const int MaxUserIdLength = 128;
    public const int MaxUserNameLength = 256;
    public const int MaxTenantNameLength = 128;
    public const int MaxCorrelationIdLength = 128;
    public const int MaxTraceIdLength = 128;
    public const int MaxHttpMethodLength = 16;
    public const int MaxPathLength = 512;
    public const int MaxUserAgentLength = 512;
    public const int MaxClientIpLength = 128;
    public const int MaxClientIdLength = 128;
    public const int MaxClientNameLength = 128;
    public const int MaxCommentLength = 1024;
    public const int MaxCommentCount = 32;
    public const int MaxCommentsPayloadLength = 2048;
    public const int MaxExceptionCount = 16;
    public const int MaxExceptionTypeLength = 256;
    public const int MaxExceptionMessageLength = 2048;
    public const int MaxExceptionStackTraceLength = 8192;
    public const int MaxExceptionsPayloadLength = 32768;
    public const int MaxActionNameLength = 128;
    public const int MaxActionDisplayNameLength = 256;
    public const int MaxActionResultLength = 64;
    public const int MaxActionParameterCount = 32;
    public const int MaxActionParameterKeyLength = 128;
    public const int MaxActionParameterValueLength = 2048;
    public const int MaxEntityTypeLength = 256;
    public const int MaxEntityIdLength = 256;
    public const int MaxChangeTypeLength = 64;
    public const int MaxPropertyNameLength = 128;
    public const int MaxPropertyTypeLength = 256;
    public const int MaxPropertyValueLength = 4096;
    public const int MaxExtraPropertyCount = 64;
    public const int MaxExtraPropertyKeyLength = 128;
    public const int MaxExtraPropertyValueLength = 2048;
    public const int MaxActionCount = 64;
    public const int MaxEntityChangeCount = 512;
    public const int MaxPropertyChangeCountPerEntity = 256;
    public const int MaxPayloadBytes = 128 * 1024;

    public static AuditLogWriteRequest Normalize(
        AuditLogWriteRequest request,
        string defaultSourceService)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.RedactionApplied)
        {
            throw new InvalidOperationException("Audit log payload must be redacted before publishing.");
        }

        if (request.RedactionVersion <= 0)
        {
            throw new InvalidOperationException("RedactionVersion must be greater than zero.");
        }

        var sourceService = NormalizeOptional(
            string.IsNullOrWhiteSpace(request.SourceService)
                ? defaultSourceService
                : request.SourceService,
            nameof(request.SourceService),
            MaxSourceServiceLength);
        if (string.IsNullOrWhiteSpace(sourceService))
        {
            throw new InvalidOperationException("AuditLogging:ApplicationName is required.");
        }

        var category = NormalizeRequired(request.Category, nameof(request.Category), MaxCategoryLength);
        var actions = NormalizeActions(request.Actions);
        var entityChanges = NormalizeEntityChanges(request.EntityChanges);
        var exceptions = NormalizeExceptions(request.Exceptions);
        var comments = NormalizeComments(request.Comments);
        var extraProperties = NormalizeExtraProperties(request.ExtraProperties);
        var occurredAtUtc = NormalizeOccurredAtUtc(request.OccurredAtUtc);
        var exceptionsPayload = exceptions.Count == 0 ? null : JsonSerializer.Serialize(exceptions);
        if (exceptionsPayload is { Length: > MaxExceptionsPayloadLength })
        {
            throw new InvalidOperationException(
                $"Exceptions payload length must be equal to or lower than {MaxExceptionsPayloadLength}.");
        }

        var commentsPayload = comments.Count == 0 ? null : JsonSerializer.Serialize(comments);
        if (commentsPayload is { Length: > MaxCommentsPayloadLength })
        {
            throw new InvalidOperationException(
                $"Comments payload length must be equal to or lower than {MaxCommentsPayloadLength}.");
        }

        var normalized = new AuditLogWriteRequest
        {
            TenantId = request.TenantId.HasValue && request.TenantId.Value != Guid.Empty
                ? request.TenantId.Value
                : null
        };
        normalized.EventId = request.EventId == Guid.Empty ? Guid.CreateVersion7() : request.EventId;
        normalized.OccurredAtUtc = occurredAtUtc;
        normalized.SourceService = sourceService;
        normalized.Category = category;
        normalized.UserId = NormalizeOptional(request.UserId, nameof(request.UserId), MaxUserIdLength);
        normalized.UserName = NormalizeOptional(request.UserName, nameof(request.UserName), MaxUserNameLength);
        normalized.TenantName = NormalizeOptional(request.TenantName, nameof(request.TenantName), MaxTenantNameLength);
        normalized.ImpersonatorUserId = NormalizeOptional(request.ImpersonatorUserId, nameof(request.ImpersonatorUserId), MaxUserIdLength);
        normalized.ImpersonatorTenantId = request.ImpersonatorTenantId;
        normalized.ImpersonatorUserName = NormalizeOptional(request.ImpersonatorUserName, nameof(request.ImpersonatorUserName), MaxUserNameLength);
        normalized.ImpersonatorTenantName = NormalizeOptional(request.ImpersonatorTenantName, nameof(request.ImpersonatorTenantName), MaxTenantNameLength);
        normalized.ClientIpAddress = NormalizeOptional(request.ClientIpAddress, nameof(request.ClientIpAddress), MaxClientIpLength);
        normalized.ClientId = NormalizeOptional(request.ClientId, nameof(request.ClientId), MaxClientIdLength);
        normalized.ClientName = NormalizeOptional(request.ClientName, nameof(request.ClientName), MaxClientNameLength);
        normalized.CorrelationId = NormalizeOptional(request.CorrelationId, nameof(request.CorrelationId), MaxCorrelationIdLength);
        normalized.TraceId = NormalizeOptional(request.TraceId, nameof(request.TraceId), MaxTraceIdLength);
        normalized.HttpMethod = NormalizeOptional(request.HttpMethod, nameof(request.HttpMethod), MaxHttpMethodLength);
        normalized.Path = NormalizeOptional(request.Path, nameof(request.Path), MaxPathLength);
        normalized.UserAgent = NormalizeOptional(request.UserAgent, nameof(request.UserAgent), MaxUserAgentLength);
        normalized.StatusCode = request.StatusCode;
        normalized.DurationMs = request.DurationMs.HasValue && request.DurationMs.Value < 0 ? 0 : request.DurationMs;
        normalized.ExtraProperties = extraProperties;
        normalized.Comments = comments;
        normalized.Exceptions = exceptions;
        normalized.Actions = actions;
        normalized.EntityChanges = entityChanges;
        normalized.IsTruncated = request.IsTruncated;
        normalized.RedactionApplied = request.RedactionApplied;
        normalized.RedactionVersion = request.RedactionVersion;
        normalized.SignatureKeyId = request.SignatureKeyId;
        normalized.SignatureTimestampUtc = request.SignatureTimestampUtc;
        normalized.Signature = request.Signature;

        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                normalized.SourceService,
                normalized.Category,
                normalized.UserId,
                normalized.UserName,
                normalized.TenantName,
                normalized.ImpersonatorUserId,
                normalized.ImpersonatorTenantId,
                normalized.ImpersonatorUserName,
                normalized.ImpersonatorTenantName,
                normalized.ClientIpAddress,
                normalized.ClientId,
                normalized.ClientName,
                normalized.CorrelationId,
                normalized.TraceId,
                normalized.HttpMethod,
                normalized.Path,
                normalized.UserAgent,
                normalized.StatusCode,
                normalized.DurationMs,
                normalized.ExtraProperties,
                commentsPayload,
                exceptionsPayload,
                normalized.Actions,
                normalized.EntityChanges,
                normalized.RedactionApplied,
                normalized.RedactionVersion
            }).Length;

        normalized.PayloadBytes = Math.Max(payloadBytes, request.PayloadBytes);
        if (normalized.PayloadBytes > MaxPayloadBytes)
        {
            throw new InvalidOperationException(
                $"Audit log payload bytes must be equal to or lower than {MaxPayloadBytes}.");
        }

        return normalized;
    }

    private static List<AuditLogWriteRequestException> NormalizeExceptions(
        List<AuditLogWriteRequestException>? exceptions)
    {
        if (exceptions == null || exceptions.Count == 0)
        {
            return [];
        }

        if (exceptions.Count > MaxExceptionCount)
        {
            throw new InvalidOperationException($"Exceptions count must be equal to or lower than {MaxExceptionCount}.");
        }

        var normalized = new List<AuditLogWriteRequestException>(exceptions.Count);
        foreach (var exception in exceptions)
        {
            if (exception == null)
            {
                continue;
            }

            normalized.Add(new AuditLogWriteRequestException
            {
                Type = NormalizeRequired(exception.Type, nameof(exception.Type), MaxExceptionTypeLength),
                Message = NormalizeRequired(exception.Message, nameof(exception.Message), MaxExceptionMessageLength),
                StackTrace = NormalizeOptional(exception.StackTrace, nameof(exception.StackTrace), MaxExceptionStackTraceLength)
            });
        }

        return normalized;
    }

    private static List<string> NormalizeComments(List<string>? comments)
    {
        if (comments == null || comments.Count == 0)
        {
            return [];
        }

        if (comments.Count > MaxCommentCount)
        {
            throw new InvalidOperationException($"Comments count must be equal to or lower than {MaxCommentCount}.");
        }

        var normalized = new List<string>(comments.Count);
        foreach (var comment in comments)
        {
            var commentText = NormalizeRequired(comment, nameof(comment), MaxCommentLength);
            normalized.Add(commentText);
        }

        return normalized;
    }

    private static List<AuditLogWriteRequestAction> NormalizeActions(
        List<AuditLogWriteRequestAction>? actions)
    {
        if (actions == null || actions.Count == 0)
        {
            return [];
        }

        if (actions.Count > MaxActionCount)
        {
            throw new InvalidOperationException(
                $"Actions count must be equal to or lower than {MaxActionCount}.");
        }

        var normalized = new List<AuditLogWriteRequestAction>(actions.Count);
        foreach (var action in actions)
        {
            if (action == null)
            {
                continue;
            }

            var parameters = NormalizeActionParameters(action.Parameters);
            normalized.Add(new AuditLogWriteRequestAction
            {
                Name = NormalizeRequired(action.Name, nameof(action.Name), MaxActionNameLength),
                DisplayName = NormalizeOptional(action.DisplayName, nameof(action.DisplayName), MaxActionDisplayNameLength),
                Result = NormalizeOptional(action.Result, nameof(action.Result), MaxActionResultLength),
                DurationMs = action.DurationMs.HasValue && action.DurationMs.Value < 0
                    ? 0
                    : action.DurationMs,
                Parameters = parameters
            });
        }

        return normalized;
    }

    private static Dictionary<string, string?> NormalizeActionParameters(Dictionary<string, string?>? parameters)
    {
        if (parameters == null || parameters.Count == 0)
        {
            return new Dictionary<string, string?>(StringComparer.Ordinal);
        }

        if (parameters.Count > MaxActionParameterCount)
        {
            throw new InvalidOperationException(
                $"Action parameter count must be equal to or lower than {MaxActionParameterCount}.");
        }

        var normalized = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var pair in parameters)
        {
            var key = NormalizeOptional(pair.Key, nameof(pair.Key), MaxActionParameterKeyLength);
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            normalized[key] = NormalizeOptional(
                pair.Value,
                $"{nameof(pair.Value)}[{key}]",
                MaxActionParameterValueLength);
        }

        return normalized;
    }

    private static List<AuditLogWriteRequestEntityChange> NormalizeEntityChanges(
        List<AuditLogWriteRequestEntityChange>? entityChanges)
    {
        if (entityChanges == null || entityChanges.Count == 0)
        {
            return [];
        }

        if (entityChanges.Count > MaxEntityChangeCount)
        {
            throw new InvalidOperationException(
                $"EntityChanges count must be equal to or lower than {MaxEntityChangeCount}.");
        }

        var normalized = new List<AuditLogWriteRequestEntityChange>(entityChanges.Count);
        foreach (var entityChange in entityChanges)
        {
            if (entityChange == null)
            {
                continue;
            }

            var propertyChanges = NormalizePropertyChanges(entityChange.PropertyChanges);
            normalized.Add(new AuditLogWriteRequestEntityChange
            {
                EntityType = NormalizeRequired(entityChange.EntityType, nameof(entityChange.EntityType), MaxEntityTypeLength),
                EntityId = NormalizeOptional(entityChange.EntityId, nameof(entityChange.EntityId), MaxEntityIdLength),
                ChangeType = NormalizeRequired(entityChange.ChangeType, nameof(entityChange.ChangeType), MaxChangeTypeLength),
                TenantId = entityChange.TenantId,
                UserId = NormalizeOptional(entityChange.UserId, nameof(entityChange.UserId), MaxUserIdLength),
                ChangeTimeUtc = entityChange.ChangeTimeUtc.HasValue
                    ? NormalizeOccurredAtUtc(entityChange.ChangeTimeUtc.Value)
                    : null,
                PropertyChanges = propertyChanges
            });
        }

        return normalized;
    }

    private static List<AuditLogWriteRequestPropertyChange> NormalizePropertyChanges(
        List<AuditLogWriteRequestPropertyChange>? propertyChanges)
    {
        if (propertyChanges == null || propertyChanges.Count == 0)
        {
            return [];
        }

        if (propertyChanges.Count > MaxPropertyChangeCountPerEntity)
        {
            throw new InvalidOperationException(
                $"PropertyChanges count must be equal to or lower than {MaxPropertyChangeCountPerEntity}.");
        }

        var normalized = new List<AuditLogWriteRequestPropertyChange>(propertyChanges.Count);
        foreach (var propertyChange in propertyChanges)
        {
            if (propertyChange == null)
            {
                continue;
            }

            normalized.Add(new AuditLogWriteRequestPropertyChange
            {
                PropertyName = NormalizeRequired(
                    propertyChange.PropertyName,
                    nameof(propertyChange.PropertyName),
                    MaxPropertyNameLength),
                PropertyType = NormalizeOptional(
                    propertyChange.PropertyType,
                    nameof(propertyChange.PropertyType),
                    MaxPropertyTypeLength),
                OriginalValue = NormalizeOptional(
                    propertyChange.OriginalValue,
                    nameof(propertyChange.OriginalValue),
                    MaxPropertyValueLength),
                NewValue = NormalizeOptional(
                    propertyChange.NewValue,
                    nameof(propertyChange.NewValue),
                    MaxPropertyValueLength)
            });
        }

        return normalized;
    }

    private static Dictionary<string, string?> NormalizeExtraProperties(Dictionary<string, string?>? extraProperties)
    {
        if (extraProperties == null || extraProperties.Count == 0)
        {
            return new Dictionary<string, string?>(StringComparer.Ordinal);
        }

        if (extraProperties.Count > MaxExtraPropertyCount)
        {
            throw new InvalidOperationException(
                $"ExtraProperties count must be equal to or lower than {MaxExtraPropertyCount}.");
        }

        var normalized = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var pair in extraProperties)
        {
            var key = NormalizeOptional(pair.Key, nameof(pair.Key), MaxExtraPropertyKeyLength);
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            normalized[key] = NormalizeOptional(
                pair.Value,
                $"{nameof(pair.Value)}[{key}]",
                MaxExtraPropertyValueLength);
        }

        return normalized;
    }

    private static string NormalizeRequired(string? value, string paramName, int maxLength)
    {
        var normalized = NormalizeOptional(value, paramName, maxLength);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException($"{paramName} is required.");
        }

        return normalized;
    }

    private static string? NormalizeOptional(string? value, string paramName, int maxLength)
    {
        var normalized = value?.Trim();
        if (normalized is { Length: > 0 } && normalized.Length > maxLength)
        {
            throw new InvalidOperationException(
                $"{paramName} length must be equal to or lower than {maxLength}.");
        }

        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static DateTime NormalizeOccurredAtUtc(DateTime occurredAtUtc)
    {
        if (occurredAtUtc == default)
        {
            return DateTime.UtcNow;
        }

        return occurredAtUtc.Kind switch
        {
            DateTimeKind.Utc => occurredAtUtc,
            DateTimeKind.Local => occurredAtUtc.ToUniversalTime(),
            _ => DateTime.SpecifyKind(occurredAtUtc, DateTimeKind.Utc)
        };
    }
}
