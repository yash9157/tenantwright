using System.Diagnostics;
using System.Diagnostics.Metrics;
using AuditLogs.Application.Contracts.Auditing;
using AuditLogs.Domain.Auditing;
using Microsoft.EntityFrameworkCore;
using Shared.EventBus.Messaging;
using Shared.EventContracts;
using Shared.AuditLogs.Publishing;
using Shared.Domain.Repositories;
using Shared.Modularity.DependencyInjection;
using Shared.Uow.Abstractions;

namespace AuditLogs.Application.Events;

[ExposeServices(
    typeof(ITenantWrightMessageHandler<AuditLogWriteRequestedTenantEto>),
    typeof(ITenantWrightMessageHandler<AuditLogWriteRequestedHostEto>),
    IncludeSelf = true)]
public sealed class AuditLogWriteRequestedEventHandler
    : ITenantWrightMessageHandler<AuditLogWriteRequestedTenantEto>,
        ITenantWrightMessageHandler<AuditLogWriteRequestedHostEto>,
        ITransientDependency
{
    private static readonly Meter Meter = new("AuditLogs.Ingestion");
    private static readonly Counter<long> IngestedCounter = Meter.CreateCounter<long>("audit.ingestion.accepted");
    private static readonly Counter<long> DuplicateCounter = Meter.CreateCounter<long>("audit.ingestion.duplicate");
    private static readonly Histogram<double> IngestionDurationMs =
        Meter.CreateHistogram<double>("audit.ingestion.duration.ms");
    private static readonly Histogram<double> QueueLagMs =
        Meter.CreateHistogram<double>("audit.ingestion.queue.lag.ms");

    private readonly IUnitOfWorkManager _uowManager;
    private readonly IRepository<AuditLog, long> _auditLogRepository;
    private readonly AuditLogWriteInfoToEntityConverter _converter;
    private readonly AuditLogEventSigner _signer;

    public AuditLogWriteRequestedEventHandler(
        IUnitOfWorkManager uowManager,
        IRepository<AuditLog, long> auditLogRepository,
        AuditLogWriteInfoToEntityConverter converter,
        AuditLogEventSigner signer)
    {
        _uowManager = uowManager;
        _auditLogRepository = auditLogRepository;
        _converter = converter;
        _signer = signer;
    }

    public Task HandleAsync(
        AuditLogWriteRequestedTenantEto eventData,
        CancellationToken cancellationToken = default)
        => HandleAsync(AuditLogContractMapper.ToRequest(eventData), cancellationToken);

    public Task HandleAsync(
        AuditLogWriteRequestedHostEto eventData,
        CancellationToken cancellationToken = default)
        => HandleAsync(AuditLogContractMapper.ToRequest(eventData), cancellationToken);

    private async Task HandleAsync(
        AuditLogWriteRequest eventData,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        var started = Stopwatch.GetTimestamp();
        _signer.Validate(eventData);
        var normalized = AuditLogWriteRequestValidation.Normalize(
            eventData,
            eventData.SourceService ?? string.Empty);

        var queueLagMs = Math.Max(0, (DateTime.UtcNow - normalized.OccurredAtUtc).TotalMilliseconds);
        QueueLagMs.Record(queueLagMs);

        var queryable = await _auditLogRepository.GetQueryableAsync();
        var alreadyRecorded = await queryable
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(x => x.EventId == normalized.EventId, cancellationToken);
        if (alreadyRecorded)
        {
            DuplicateCounter.Add(
                1,
                new KeyValuePair<string, object?>("source_service", normalized.SourceService),
                new KeyValuePair<string, object?>("duplicate_reason", "event_id_unique_constraint"));
            return;
        }

        var writeModel = _converter.Convert(normalized, normalized.TenantId);
        PopulateAggregateGraph(writeModel);
        await _auditLogRepository.InsertAsync(writeModel.AuditLog, autoSave: false, cancellationToken);

        // The messaging inbox transaction owns this unit of work; the metrics describe committed ingestion only.
        var unitOfWork = _uowManager.Current
            ?? throw new InvalidOperationException("Audit ingestion requires an active unit of work.");
        unitOfWork.OnCompleted(() =>
        {
            IngestedCounter.Add(
                1,
                new KeyValuePair<string, object?>("source_service", normalized.SourceService));
            IngestionDurationMs.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return Task.CompletedTask;
        });
    }

    private static void PopulateAggregateGraph(AuditLogWriteEntityModel writeModel)
    {
        foreach (var action in writeModel.Actions)
        {
            writeModel.AuditLog.Actions.Add(new AuditLogAction(
                id: 0,
                auditLogId: 0,
                tenantId: writeModel.AuditLog.TenantId,
                serviceName: action.ServiceName,
                methodName: action.MethodName,
                parameters: action.Parameters,
                executionTime: action.ExecutionTime,
                executionDuration: action.ExecutionDuration,
                result: action.Result));
        }

        foreach (var sourceChange in writeModel.EntityChanges)
        {
            var entityChange = new EntityChange(
                id: 0,
                auditLogId: 0,
                tenantId: sourceChange.TenantId,
                changeTime: sourceChange.ChangeTime,
                changeType: sourceChange.ChangeType,
                entityTenantId: sourceChange.EntityTenantId,
                entityId: sourceChange.EntityId,
                entityTypeFullName: sourceChange.EntityTypeFullName);

            foreach (var property in sourceChange.PropertyChanges)
            {
                entityChange.PropertyChanges.Add(new EntityPropertyChange(
                    id: 0,
                    entityChangeId: 0,
                    tenantId: writeModel.AuditLog.TenantId,
                    propertyName: property.PropertyName,
                    propertyTypeFullName: property.PropertyType,
                    originalValue: property.OriginalValue,
                    newValue: property.NewValue));
            }

            writeModel.AuditLog.EntityChanges.Add(entityChange);
        }
    }
}
