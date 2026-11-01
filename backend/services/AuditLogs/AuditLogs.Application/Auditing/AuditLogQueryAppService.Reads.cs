using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuditArchiveManifestStatus = AuditLogs.Domain.Shared.Auditing.AuditArchiveManifestStatus;
using AuditLogFilter = AuditLogs.Domain.Shared.Auditing.AuditLogFilter;
using AuditLogExportFormat = AuditLogs.Domain.Shared.Auditing.AuditLogExportFormat;
using AuditLogExportStatus = AuditLogs.Domain.Shared.Auditing.AuditLogExportStatus;
using EntityChangeFilter = AuditLogs.Domain.Shared.Auditing.EntityChangeFilter;
using AuditLogs.Application.Contracts.Auditing;
using AuditLogs.Domain.Auditing;
using AuditLogsErrorCodes = AuditLogs.Domain.Shared.Auditing.AuditLogsErrorCodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shared;
using Shared.AuditLogs.Auditing;
using Shared.AuditLogs.Publishing;
using Shared.CurrentUser;
using Shared.Data.Filtering;
using Shared.Domain;
using Shared.Domain.Repositories;
using Shared.EntityFrameworkCore.TenantDatabases;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;

namespace AuditLogs.Application.Auditing;

public sealed partial class AuditLogQueryAppService
{
    /// <summary>
    /// Cursor page of audit logs, newest first, across every database that holds matching rows (see
    /// <see cref="QueryAuditDatabasesAsync{T}"/>): each database returns its first page after the cursor and the
    /// merged page is cut from their union in the same (ExecutionTime, Id) order, so the cursor stays valid.
    /// </summary>
    public async Task<PagedResultDto<AuditLogSummaryDto>> GetListAsync(GetAuditLogsInput input)
    {
        EnsureHostOperatorAccess();
        input ??= new GetAuditLogsInput();
        var maxResultCount = Math.Clamp(input.MaxResultCount, 1, 200);
        EnsureCursorPaging(input.SkipCount);
        var resolvedTenantId = ResolveTenantFilter(input.TenantId);
        var filter = new AuditLogFilter
            {
                TenantId = resolvedTenantId,
                FromUtc = input.FromUtc,
                ToUtc = input.ToUtc,
                UserId = NormalizeOptional(input.UserId, AuditLogConsts.MaxUserIdLength),
                ApplicationName = NormalizeOptional(input.ApplicationName, AuditLogConsts.MaxApplicationNameLength),
                Category = NormalizeOptional(input.Category, AuditLogConsts.MaxCategoryLength),
                UrlContains = NormalizeOptional(input.UrlContains, AuditLogConsts.MaxUrlLength),
                HttpStatusCode = input.HttpStatusCode,
                ClientIpAddress = NormalizeOptional(input.ClientIpAddress, AuditLogConsts.MaxClientIpAddressLength),
                CorrelationId = NormalizeOptional(input.CorrelationId, AuditLogConsts.MaxCorrelationIdLength),
                TraceId = NormalizeOptional(input.TraceId, AuditLogConsts.MaxTraceIdLength)
            };

        var started = Stopwatch.GetTimestamp();
        var pages = await QueryAuditDatabasesAsync(
            resolvedTenantId,
            "auditlogs-console-list",
            async () =>
            {
                var query = (await _queryStore.GetListQueryableAsync(filter, includeDetails: false))
                    .WhereOwnedByCurrentDatabase();

                // Opt-in: this counts every row matching the filter, so it is a scan of the largest table in
                // the system on every page turn. Cursor paging navigates by NextCursor and never needs it, and
                // UrlContains makes the same scan unindexed on top of that.
                long? databaseTotal = input.IncludeTotalCount ? await query.LongCountAsync() : null;
                var databaseItems = await AuditLogCursorCodec.ApplyAuditLogCursor(query, input.Cursor)
                    .OrderByDescending(x => x.ExecutionTime)
                    .ThenByDescending(x => x.Id)
                    .Take(maxResultCount + 1)
                    .Select(x => new AuditLogSummaryDto
                    {
                        Id = x.Id,
                        EventId = x.EventId,
                        TenantId = x.TenantId,
                        ExecutionTime = x.ExecutionTime,
                        ExecutionDuration = x.ExecutionDuration,
                        ApplicationName = x.ApplicationName,
                        Category = x.Category,
                        UserId = x.UserId,
                        UserName = x.UserName,
                        HttpMethod = x.HttpMethod,
                        Url = x.Url,
                        HttpStatusCode = x.HttpStatusCode
                    })
                    .ToListAsync();
                return (TotalCount: databaseTotal, Items: databaseItems);
            });

        var items = pages
            .SelectMany(page => page.Items)
            .OrderByDescending(x => x.ExecutionTime)
            .ThenByDescending(x => x.Id)
            .Take(maxResultCount + 1)
            .ToList();
        long? totalCount = input.IncludeTotalCount ? pages.Sum(page => page.TotalCount ?? 0) : null;
        var hasMore = items.Count > maxResultCount;
        if (hasMore)
        {
            items.RemoveAt(items.Count - 1);
        }

        ListQueryDurationMs.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);

        return new PagedResultDto<AuditLogSummaryDto>
        {
            TotalCount = totalCount,
            Items = items,
            NextCursor = hasMore && items.Count > 0
                ? AuditLogCursorCodec.Encode(items[^1].ExecutionTime, items[^1].Id)
                : null
        };
    }

    public async Task<AuditLogDetailDto> GetAsync(long id, Guid? tenantId = null)
    {
        return await ExecuteInAuditRowDatabaseAsync(tenantId, async () =>
        {
            var auditLog = await FindAuditLogDetailsAsync(id, tenantId);

            if (_currentTenant.Id.HasValue && auditLog.TenantId != _currentTenant.Id.Value)
            {
                throw new UnauthorizedAccessException("Cross-tenant audit access is not allowed.");
            }

            var actions = auditLog.Actions
                .OrderBy(x => x.ExecutionTime)
                .Select(x => new AuditLogActionDto
                {
                    Id = x.Id,
                    ServiceName = x.ServiceName,
                    MethodName = x.MethodName,
                    Parameters = "Sensitive payload hidden.",
                    ExecutionTime = x.ExecutionTime,
                    ExecutionDuration = x.ExecutionDuration,
                    Result = null
                })
                .ToList();

            var entityChanges = auditLog.EntityChanges
                .OrderBy(x => x.ChangeTime)
                .Select(MapEntityChangeDetail)
                .ToList();

            var signaturePayload = new AuditLogSignaturePayload(
                auditLog.EventId,
                auditLog.TenantId,
                auditLog.ExecutionTime,
                auditLog.ApplicationName,
                auditLog.Category,
                auditLog.UserId,
                auditLog.CorrelationId,
                auditLog.Url,
                ParseIntExtraProperty(auditLog.ExtraProperties, AuditLogs.Domain.Shared.Auditing.AuditLogExtraPropertyNames.PayloadBytes) ?? 0,
                ParseBoolExtraProperty(auditLog.ExtraProperties, AuditLogs.Domain.Shared.Auditing.AuditLogExtraPropertyNames.RedactionApplied),
                ParseIntExtraProperty(auditLog.ExtraProperties, AuditLogs.Domain.Shared.Auditing.AuditLogExtraPropertyNames.RedactionVersion) ?? 0);
            var signatureVerified = _signer.VerifyPersisted(
                signaturePayload,
                auditLog.SignatureKeyId,
                auditLog.SignatureTimestampUtc,
                auditLog.Signature);

            var detail = new AuditLogDetailDto
            {
                Id = auditLog.Id,
                EventId = auditLog.EventId,
                TenantId = auditLog.TenantId,
                SignatureVerified = signatureVerified,
                ExecutionTime = auditLog.ExecutionTime,
                ExecutionDuration = auditLog.ExecutionDuration,
                ApplicationName = auditLog.ApplicationName,
                Category = auditLog.Category,
                UserId = auditLog.UserId,
                UserName = auditLog.UserName,
                HttpMethod = auditLog.HttpMethod,
                Url = auditLog.Url,
                HttpStatusCode = auditLog.HttpStatusCode,
                TenantName = auditLog.TenantName,
                ImpersonatorUserId = auditLog.ImpersonatorUserId,
                ImpersonatorUserName = auditLog.ImpersonatorUserName,
                ImpersonatorTenantId = auditLog.ImpersonatorTenantId,
                ImpersonatorTenantName = auditLog.ImpersonatorTenantName,
                ClientIpAddress = auditLog.ClientIpAddress,
                ClientName = auditLog.ClientName,
                ClientId = auditLog.ClientId,
                CorrelationId = auditLog.CorrelationId,
                TraceId = auditLog.TraceId,
                BrowserInfo = auditLog.BrowserInfo,
                SensitivePayloadRedacted = true,
                Exceptions = [],
                Comments = DeserializeComments(auditLog.Comments),
                Actions = actions,
                EntityChanges = entityChanges,
                ExtraProperties = new Dictionary<string, string?>(StringComparer.Ordinal)
            };

            return detail;
        });
    }

    public async Task<AuditLogSensitivePayloadDto> GetSensitivePayloadAsync(long id, Guid? tenantId = null)
    {
        return await ExecuteInAuditRowDatabaseAsync(tenantId, async () =>
        {
            var auditLog = await FindAuditLogDetailsAsync(id, tenantId);

            return new AuditLogSensitivePayloadDto
            {
                AuditLogId = auditLog.Id,
                Exceptions = DeserializeExceptions(auditLog.Exceptions),
                ExtraProperties = auditLog.ExtraProperties.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value?.ToString(),
                    StringComparer.Ordinal),
                Actions = auditLog.Actions
                    .OrderBy(x => x.ExecutionTime)
                    .Select(x => new AuditLogSensitiveActionPayloadDto
                    {
                        ActionId = x.Id,
                        Parameters = x.Parameters,
                        Result = x.Result
                    })
                    .ToList()
            };
        });
    }

    /// <summary>Cursor page of an entity's changes, merged across databases like <see cref="GetListAsync"/>.</summary>
    public async Task<PagedResultDto<AuditLogEntityChangeDto>> GetEntityChangesAsync(GetAuditLogEntityChangesInput input)
    {
        EnsureHostOperatorAccess();
        input ??= new GetAuditLogEntityChangesInput();
        if (string.IsNullOrWhiteSpace(input.EntityType))
        {
            throw CreateValidationException(AuditLogsErrorCodes.EntityTypeRequired, "EntityType is required.");
        }

        if (string.IsNullOrWhiteSpace(input.EntityId))
        {
            throw CreateValidationException(AuditLogsErrorCodes.EntityIdRequired, "EntityId is required.");
        }

        var maxResultCount = Math.Clamp(input.MaxResultCount, 1, 200);
        EnsureCursorPaging(input.SkipCount);
        var entityType = input.EntityType.Trim();
        var entityId = input.EntityId.Trim();
        var resolvedTenantId = ResolveTenantFilter(input.TenantId);
        var filter = new EntityChangeFilter
        {
            TenantId = resolvedTenantId,
            EntityTypeFullName = entityType,
            EntityId = entityId,
            FromUtc = input.FromUtc,
            ToUtc = input.ToUtc
        };

        var started = Stopwatch.GetTimestamp();
        var pages = await QueryAuditDatabasesAsync(
            resolvedTenantId,
            "auditlogs-console-entity-changes",
            async () =>
            {
                var query = (await _queryStore.GetEntityChangeQueryableAsync(filter, includeDetails: false))
                    .WhereOwnedByCurrentDatabase();

                // Same reasoning as GetListAsync: counting every change recorded against the entity is a scan
                // the cursor grid never reads, and an entity with a long history is exactly where it hurts.
                long? databaseTotal = input.IncludeTotalCount ? await query.LongCountAsync() : null;
                var auditLogQuery = await _queryStore.GetListQueryableAsync(
                    new AuditLogFilter { TenantId = resolvedTenantId },
                    includeDetails: false);

                var databaseItems = await AuditLogCursorCodec.ApplyEntityChangeCursor(query, input.Cursor)
                    .OrderByDescending(x => x.ChangeTime)
                    .ThenByDescending(x => x.Id)
                    .Take(maxResultCount + 1)
                    .Join(
                        auditLogQuery,
                        entityChange => entityChange.AuditLogId,
                        auditLog => auditLog.Id,
                        (entityChange, auditLog) => new AuditLogEntityChangeDto
                        {
                            Id = entityChange.Id,
                            AuditLogId = entityChange.AuditLogId,
                            TenantId = entityChange.TenantId,
                            ChangeTime = entityChange.ChangeTime,
                            ExecutionTime = auditLog.ExecutionTime,
                            EntityTypeFullName = entityChange.EntityTypeFullName,
                            EntityId = entityChange.EntityId,
                            ChangeType = entityChange.ChangeType
                        })
                    .OrderByDescending(x => x.ChangeTime)
                    .ThenByDescending(x => x.Id)
                    .ToListAsync();
                return (TotalCount: databaseTotal, Items: databaseItems);
            });

        var items = pages
            .SelectMany(page => page.Items)
            .OrderByDescending(x => x.ChangeTime)
            .ThenByDescending(x => x.Id)
            .Take(maxResultCount + 1)
            .ToList();
        long? totalCount = input.IncludeTotalCount ? pages.Sum(page => page.TotalCount ?? 0) : null;
        var hasMore = items.Count > maxResultCount;
        if (hasMore)
        {
            items.RemoveAt(items.Count - 1);
        }

        EntityChangesQueryDurationMs.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);

        return new PagedResultDto<AuditLogEntityChangeDto>
        {
            TotalCount = totalCount,
            Items = items,
            NextCursor = hasMore && items.Count > 0
                ? AuditLogCursorCodec.Encode(items[^1].ChangeTime, items[^1].Id)
                : null
        };
    }

    public async Task<AuditLogEntityChangeDetailDto> GetEntityChangeAsync(long id, Guid tenantId)
    {
        if (tenantId == Guid.Empty)
        {
            throw CreateValidationException(AuditLogsErrorCodes.TenantIdRequired, "TenantId is required.");
        }

        return await ExecuteInHostTargetTenantDatabaseAsync(tenantId, async () =>
        {
            var resolvedTenantId = ResolveTenantFilter(tenantId);
            if (!resolvedTenantId.HasValue)
            {
                throw new UnauthorizedAccessException("A tenant scope is required for entity-change access.");
            }

            var entityChange = await _queryStore.FindEntityChangeDetailsAsync(id, resolvedTenantId.Value);
            if (entityChange == null)
            {
                throw CreateNotFoundException(AuditLogsErrorCodes.AuditEntityChangeNotFound, "Audit entity change not found.");
            }

            if (entityChange.TenantId != resolvedTenantId.Value)
            {
                throw new UnauthorizedAccessException("Cross-tenant entity-change access is not allowed.");
            }

            return MapEntityChangeDetail(entityChange);
        });
    }

    /// <summary>
    /// Audit log ids are identity values, unique per database only: with <paramref name="tenantId"/> the read runs in
    /// that tenant's database (its own when dedicated); without it in the shared database, which holds host events
    /// and every tenant that has no dedicated AuditLogs database.
    /// </summary>
    private Task<T> ExecuteInAuditRowDatabaseAsync<T>(Guid? tenantId, Func<Task<T>> action) =>
        tenantId.HasValue
            ? ExecuteInHostTargetTenantDatabaseAsync(tenantId.Value, action)
            : ExecuteWithHostMultiTenantFilterDisabledAsync(action);

    private async Task<AuditLog> FindAuditLogDetailsAsync(long id, Guid? tenantId)
    {
        var auditLog = await _queryStore.FindDetailsAsync(id);
        if (auditLog == null || (tenantId.HasValue && auditLog.TenantId != tenantId.Value))
        {
            throw CreateNotFoundException(AuditLogsErrorCodes.AuditLogNotFound, "Audit log not found.");
        }

        return auditLog;
    }

    private static AuditLogEntityChangeDetailDto MapEntityChangeDetail(EntityChange entityChange)
    {
        return new AuditLogEntityChangeDetailDto
        {
            Id = entityChange.Id,
            ChangeTime = entityChange.ChangeTime,
            ChangeType = entityChange.ChangeType,
            EntityTenantId = entityChange.EntityTenantId,
            EntityTypeFullName = entityChange.EntityTypeFullName,
            EntityId = entityChange.EntityId,
            PropertyChanges = entityChange.PropertyChanges
                .OrderBy(property => property.Id)
                .Select(property => new AuditLogEntityPropertyChangeDto
                {
                    Id = property.Id,
                    PropertyName = property.PropertyName,
                    PropertyTypeFullName = property.PropertyTypeFullName,
                    OriginalValue = property.OriginalValue,
                    NewValue = property.NewValue
                })
                .ToList()
        };
    }

    private static void EnsureCursorPaging(int skipCount)
    {
        if (skipCount != 0)
        {
            throw new SharedValidationException("SkipCount is not supported. Use the cursor returned by the previous page.");
        }
    }

}
