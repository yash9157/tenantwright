using System.Text.Json;
using AuditLogs.Domain.Auditing;
using AuditLogs.Domain.Shared.Auditing;
using Shared.AuditLogs.Publishing;
using Shared.EventContracts;
using Shared.Modularity.DependencyInjection;

namespace AuditLogs.Application.Events;

public sealed class AuditLogWriteInfoToEntityConverter : ITransientDependency
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal AuditLogWriteEntityModel Convert(AuditLogWriteRequestedTenantEto writeInfo, Guid? tenantId)
        => Convert(AuditLogContractMapper.ToRequest(writeInfo), tenantId);

    internal AuditLogWriteEntityModel Convert(AuditLogWriteRequestedHostEto writeInfo, Guid? tenantId)
        => Convert(AuditLogContractMapper.ToRequest(writeInfo), tenantId);

    internal AuditLogWriteEntityModel Convert(AuditLogWriteRequest writeInfo, Guid? tenantId)
    {
        ArgumentNullException.ThrowIfNull(writeInfo);

        var exceptionsPayload = writeInfo.Exceptions.Count == 0
            ? null
            : JsonSerializer.Serialize(writeInfo.Exceptions, JsonOptions);
        var commentsPayload = writeInfo.Comments.Count == 0
            ? null
            : JsonSerializer.Serialize(writeInfo.Comments, JsonOptions);

        var extraProperties = new Dictionary<string, string?>(writeInfo.ExtraProperties, StringComparer.Ordinal)
        {
            [AuditLogExtraPropertyNames.IsTruncated] = writeInfo.IsTruncated.ToString(),
            [AuditLogExtraPropertyNames.PayloadBytes] = writeInfo.PayloadBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [AuditLogExtraPropertyNames.RedactionApplied] = writeInfo.RedactionApplied.ToString(),
            [AuditLogExtraPropertyNames.RedactionVersion] = writeInfo.RedactionVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };

        var auditLog = new AuditLog(
            id: 0,
            eventId: writeInfo.EventId,
            applicationName: writeInfo.SourceService ?? string.Empty,
            category: writeInfo.Category,
            tenantId: tenantId,
            tenantName: writeInfo.TenantName,
            userId: writeInfo.UserId,
            userName: writeInfo.UserName,
            executionTime: writeInfo.OccurredAtUtc,
            executionDuration: (int)Math.Round(Math.Max(0, writeInfo.DurationMs ?? 0)),
            clientIpAddress: writeInfo.ClientIpAddress,
            clientName: writeInfo.ClientName,
            clientId: writeInfo.ClientId,
            correlationId: writeInfo.CorrelationId,
            traceId: writeInfo.TraceId,
            browserInfo: writeInfo.UserAgent,
            httpMethod: writeInfo.HttpMethod,
            url: writeInfo.Path,
            httpStatusCode: writeInfo.StatusCode,
            impersonatorUserId: writeInfo.ImpersonatorUserId,
            impersonatorUserName: writeInfo.ImpersonatorUserName,
            impersonatorTenantId: writeInfo.ImpersonatorTenantId,
            impersonatorTenantName: writeInfo.ImpersonatorTenantName,
            exceptions: exceptionsPayload,
            comments: commentsPayload,
            extraProperties: extraProperties,
            signature: writeInfo.Signature,
            signatureKeyId: writeInfo.SignatureKeyId,
            signatureTimestampUtc: writeInfo.SignatureTimestampUtc);

        var actions = writeInfo.Actions
            .Select(action => new AuditLogActionWriteModel
            {
                ServiceName = action.DisplayName ?? "unknown-service",
                MethodName = action.Name,
                Parameters = JsonSerializer.Serialize(action.Parameters, JsonOptions),
                ExecutionTime = writeInfo.OccurredAtUtc,
                ExecutionDuration = (int)Math.Round(Math.Max(0, action.DurationMs ?? writeInfo.DurationMs ?? 0)),
                Result = action.Result
            })
            .ToList();

        var entityChanges = writeInfo.EntityChanges
            .Select(change => new AuditLogEntityChangeWriteModel
            {
                TenantId = tenantId,
                ChangeTime = change.ChangeTimeUtc ?? writeInfo.OccurredAtUtc,
                ChangeType = change.ChangeType,
                EntityTenantId = change.TenantId,
                EntityId = change.EntityId ?? "unknown",
                EntityTypeFullName = change.EntityType,
                PropertyChanges = change.PropertyChanges
            })
            .ToList();

        return new AuditLogWriteEntityModel
        {
            AuditLog = auditLog,
            Actions = actions,
            EntityChanges = entityChanges
        };
    }
}

internal sealed class AuditLogWriteEntityModel
{
    public required AuditLog AuditLog { get; init; }

    public required IReadOnlyList<AuditLogActionWriteModel> Actions { get; init; }

    public required IReadOnlyList<AuditLogEntityChangeWriteModel> EntityChanges { get; init; }
}

internal sealed class AuditLogActionWriteModel
{
    public required string ServiceName { get; init; }

    public required string MethodName { get; init; }

    public required string Parameters { get; init; }

    public required DateTime ExecutionTime { get; init; }

    public required int ExecutionDuration { get; init; }

    public string? Result { get; init; }
}

internal sealed class AuditLogEntityChangeWriteModel
{
    public required Guid? TenantId { get; init; }

    public required DateTime ChangeTime { get; init; }

    public required string ChangeType { get; init; }

    public required Guid? EntityTenantId { get; init; }

    public required string EntityId { get; init; }

    public required string EntityTypeFullName { get; init; }

    public required IReadOnlyList<AuditLogWriteRequestPropertyChange> PropertyChanges { get; init; }
}
