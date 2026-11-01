using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shared.AuditLogs.AspNetCore;
using Shared.Modularity.DependencyInjection;
using Shared.AuditLogs.Publishing;

namespace Shared.AuditLogs.Auditing;

public sealed class EventBusAuditingStore : IAuditingStore, ITransientDependency
{
    private readonly IAuditLogWriteRequestPublisher _publisher;
    private readonly IOptions<SharedAuditingOptions> _options;
    private readonly IOptions<AuditRedactionOptions> _redactionOptions;
    private readonly ILogger<EventBusAuditingStore> _logger;

    public EventBusAuditingStore(
        IAuditLogWriteRequestPublisher publisher,
        IOptions<SharedAuditingOptions> options,
        IOptions<AuditRedactionOptions> redactionOptions,
        ILogger<EventBusAuditingStore> logger)
    {
        _publisher = publisher;
        _options = options;
        _redactionOptions = redactionOptions;
        _logger = logger;
    }

    public async Task SaveAsync(AuditLogInfo auditInfo)
    {
        var options = _options.Value;

        // Per-category Strict opt-in (sub-flow 3): security-critical categories fail-closed even when
        // HideErrors is on globally, so a broken audit pipeline doesn't silently swallow their audit
        // trail. Everything else stays Observe (HideErrors still applies) to avoid making the whole
        // audited surface an availability risk on the audit pipeline.
        if (!options.HideErrors || options.IsStrictFailureCategory(auditInfo.Category))
        {
            await SaveInternalAsync(auditInfo);
            return;
        }

        try
        {
            await SaveInternalAsync(auditInfo);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not persist audit log request for category {Category}", auditInfo.Category);
        }
    }

    private Task SaveInternalAsync(AuditLogInfo auditInfo)
    {
        AuditLogProducerRedaction.Apply(auditInfo, _redactionOptions.Value);

        var request = new AuditLogWriteRequest
        {
            EventId = Guid.CreateVersion7(),
            TenantId = auditInfo.TenantId,
            OccurredAtUtc = auditInfo.ExecutionTime,
            SourceService = auditInfo.ApplicationName,
            Category = string.IsNullOrWhiteSpace(auditInfo.Category) ? "http.request" : auditInfo.Category,
            UserId = auditInfo.UserId,
            UserName = auditInfo.UserName,
            TenantName = auditInfo.TenantName,
            ImpersonatorUserId = auditInfo.ImpersonatorUserId,
            ImpersonatorTenantId = auditInfo.ImpersonatorTenantId,
            ImpersonatorUserName = auditInfo.ImpersonatorUserName,
            ImpersonatorTenantName = auditInfo.ImpersonatorTenantName,
            ClientIpAddress = auditInfo.ClientIpAddress,
            ClientId = auditInfo.ClientId,
            ClientName = auditInfo.ClientName,
            CorrelationId = auditInfo.CorrelationId,
            TraceId = auditInfo.TraceId,
            HttpMethod = auditInfo.HttpMethod,
            Path = auditInfo.Url,
            UserAgent = auditInfo.BrowserInfo,
            StatusCode = auditInfo.HttpStatusCode,
            DurationMs = auditInfo.ExecutionDuration,
            ExtraProperties = new Dictionary<string, string?>(auditInfo.ExtraProperties, StringComparer.Ordinal),
            Comments = auditInfo.Comments
                .Where(comment => !string.IsNullOrWhiteSpace(comment))
                .Select(comment => comment.Trim())
                .ToList(),
            Exceptions = auditInfo.Exceptions
                .Select(ex => new AuditLogWriteRequestException
                {
                    Type = ex.Type,
                    Message = ex.Message,
                    StackTrace = ex.StackTrace
                })
                .ToList(),
            Actions = auditInfo.Actions
                .Select(action => new AuditLogWriteRequestAction
                {
                    Name = action.MethodName,
                    DisplayName = action.ServiceName,
                    Result = action.Result,
                    DurationMs = action.ExecutionDuration,
                    Parameters = DeserializeParameters(action.Parameters, action.ExtraProperties)
                })
                .ToList(),
            EntityChanges = auditInfo.EntityChanges
                .Select(change => new AuditLogWriteRequestEntityChange
                {
                    EntityType = change.EntityTypeFullName,
                    EntityId = change.EntityId,
                    ChangeType = change.ChangeType,
                    TenantId = change.TenantId,
                    UserId = change.UserId?.ToString("D"),
                    ChangeTimeUtc = change.ChangeTime,
                    PropertyChanges = change.PropertyChanges
                        .Select(property => new AuditLogWriteRequestPropertyChange
                        {
                            PropertyName = property.PropertyName,
                            PropertyType = property.PropertyTypeFullName,
                            OriginalValue = property.OriginalValue,
                            NewValue = property.NewValue
                        })
                        .ToList()
                })
                .ToList(),
            IsTruncated = auditInfo.IsTruncated,
            PayloadBytes = auditInfo.PayloadBytes,
            RedactionApplied = auditInfo.RedactionApplied,
            RedactionVersion = auditInfo.RedactionVersion
        };

        return _publisher.PublishAsync(request);
    }

    private static Dictionary<string, string?> DeserializeParameters(
        string? parameters,
        Dictionary<string, string?> extraProperties)
    {
        if (string.IsNullOrWhiteSpace(parameters))
        {
            return new Dictionary<string, string?>(extraProperties, StringComparer.Ordinal);
        }

        try
        {
            using var document = JsonDocument.Parse(
                parameters,
                new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new Dictionary<string, string?>(extraProperties, StringComparer.Ordinal);
            }

            var parsed = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                parsed[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.Null => null,
                    JsonValueKind.String => property.Value.GetString(),
                    _ => property.Value.GetRawText()
                };
            }

            foreach (var pair in extraProperties)
            {
                parsed[pair.Key] = pair.Value;
            }

            return parsed;
        }
        catch
        {
            return new Dictionary<string, string?>(extraProperties, StringComparer.Ordinal);
        }
    }
}
