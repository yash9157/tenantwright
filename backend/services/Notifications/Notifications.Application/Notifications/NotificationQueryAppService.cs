using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Notifications.Application.Contracts.Common;
using Notifications.Application.Contracts.Notifications;
using Notifications.Application.Delivery;
using Notifications.Domain.Notifications;
using Notifications.Domain.Shared.Constants;
using Notifications.Domain.Shared.Enums;
using Notifications.EntityFrameworkCore.EntityFrameworkCore;
using Shared.AuditLogs.Publishing;
using Shared.Data.Filtering;
using Shared.Domain;
using Shared;
using Shared.Domain.Entities;
using Shared.Domain.Repositories;
using Shared.EntityFrameworkCore.TenantDatabases;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;
using Shared.Permissions.Authorization;
using Shared.Permissions.Definitions.Notifications;
using Shared.Security;

namespace Notifications.Application.Notifications;

[DisableConventionalRegistration]
public sealed class NotificationQueryAppService : INotificationQueryAppService, ITransientDependency
{
    private const int DefaultChronologyPageSize = 50;
    private const int MaxChronologyPageSize = 100;
    private const string TemplateModelFieldName = nameof(NotificationDto.TemplateModelJson);

    private readonly IRepository<Notification, Guid> _notificationRepository;
    private readonly IRepository<NotificationDelivery, long> _deliveryRepository;
    private readonly IRepository<NotificationDeliveryChronology, long> _chronologyRepository;
    private readonly NotificationsQuerySecurityOptions _querySecurityOptions;
    private readonly IDataFilter _dataFilter;
    private readonly ICurrentTenant _currentTenant;
    private readonly IPermissionChecker _permissionChecker;
    private readonly ICurrentPrincipalAccessor _currentPrincipalAccessor;
    private readonly IAuditLogWriteRequestPublisher _auditLogPublisher;
    private readonly TenantDatabaseFanOut _tenantDatabases;
    private readonly NotificationDatabaseLocator _databaseLocator;

    public NotificationQueryAppService(
        IRepository<Notification, Guid> notificationRepository,
        IRepository<NotificationDelivery, long> deliveryRepository,
        IRepository<NotificationDeliveryChronology, long> chronologyRepository,
        IOptions<NotificationsQuerySecurityOptions> querySecurityOptions,
        IDataFilter dataFilter,
        ICurrentTenant currentTenant,
        IPermissionChecker permissionChecker,
        ICurrentPrincipalAccessor currentPrincipalAccessor,
        IAuditLogWriteRequestPublisher auditLogPublisher,
        TenantDatabaseFanOut tenantDatabases,
        NotificationDatabaseLocator databaseLocator)
    {
        _notificationRepository = notificationRepository;
        _deliveryRepository = deliveryRepository;
        _chronologyRepository = chronologyRepository;
        _querySecurityOptions = querySecurityOptions.Value;
        _dataFilter = dataFilter;
        _currentTenant = currentTenant;
        _permissionChecker = permissionChecker;
        _currentPrincipalAccessor = currentPrincipalAccessor;
        _auditLogPublisher = auditLogPublisher;
        _tenantDatabases = tenantDatabases;
        _databaseLocator = databaseLocator;
    }

    public async Task<PagedResultDto<NotificationListItemDto>> GetListAsync(GetNotificationsInput input)
    {
        await EnsurePermissionAsync(NotificationsPermissions.Query.Read);
        input.TenantId = ResolveTenantScope(input.TenantId);
        // A host query for one tenant reads that tenant's database (its own when dedicated). Without a tenant the
        // host list stays on host-owned notifications (TenantId null), which only the shared database holds.
        using var tenantDatabase = _tenantDatabases.EnterDatabase(await FindHostTargetTenantDatabaseAsync(input.TenantId));
        using var tenantFilterScope = BeginTenantFilterScope(input.TenantId);

        var notifications = await _notificationRepository.GetQueryableAsync();
        var deliveries = await _deliveryRepository.GetQueryableAsync();
        var query = notifications;

        if (input.TenantId.HasValue)
        {
            query = query.Where(x => x.TenantId == input.TenantId.Value);
        }

        if (input.Channel.HasValue)
        {
            query = query.Where(x => x.Channel == input.Channel.Value);
        }

        if (!string.IsNullOrWhiteSpace(input.TemplateName))
        {
            var templateName = input.TemplateName.Trim();
            query = query.Where(x => x.TemplateName.Contains(templateName));
        }

        if (input.FromUtc.HasValue)
        {
            query = query.Where(x => x.CreationTime >= input.FromUtc.Value);
        }

        if (input.ToUtc.HasValue)
        {
            query = query.Where(x => x.CreationTime <= input.ToUtc.Value);
        }

        if (input.Status.HasValue)
        {
            query = query.Where(notification => deliveries.Any(delivery =>
                delivery.NotificationId == notification.Id &&
                delivery.Status == input.Status.Value));
        }

        var totalCount = await query.LongCountAsync();
        var skip = Math.Max(0, input.SkipCount);
        var take = input.MaxResultCount <= 0 ? 50 : Math.Min(input.MaxResultCount, 100);

        var page = await query
            .AsNoTracking()
            .OrderByDescending(x => x.CreationTime)
            .ThenByDescending(x => x.Id)
            .Skip(skip)
            .Take(take)
            .Select(notification => new NotificationListItemDto
            {
                Id = notification.Id,
                RequestId = notification.RequestId,
                TenantId = notification.TenantId,
                Channel = notification.Channel,
                TemplateName = notification.TemplateName,
                SourceService = notification.SourceService,
                Subject = notification.Subject,
                Category = notification.Category,
                Priority = notification.Priority,
                ActionUrl = notification.ActionUrl,
                CreationTime = notification.CreationTime,
                ScheduledForUtc = notification.ScheduledForUtc,
                DeliveryCount = deliveries.Count(delivery => delivery.NotificationId == notification.Id),
                QueuedCount = deliveries.Count(delivery => delivery.NotificationId == notification.Id && delivery.Status == NotificationStatus.Queued),
                ProcessingCount = deliveries.Count(delivery => delivery.NotificationId == notification.Id && delivery.Status == NotificationStatus.Processing),
                ProviderAcceptedCount = deliveries.Count(delivery =>
                    delivery.NotificationId == notification.Id &&
                    delivery.Status == NotificationStatus.ProviderAccepted),
                DeliveredCount = deliveries.Count(delivery =>
                    delivery.NotificationId == notification.Id &&
                    delivery.Status == NotificationStatus.Delivered),
                FailedCount = deliveries.Count(delivery => delivery.NotificationId == notification.Id && delivery.Status == NotificationStatus.Failed),
                NeedsReviewCount = deliveries.Count(delivery => delivery.NotificationId == notification.Id && delivery.Status == NotificationStatus.NeedsReview),
                DeadLetteredCount = deliveries.Count(delivery => delivery.NotificationId == notification.Id && delivery.Status == NotificationStatus.DeadLettered)
            })
            .ToListAsync();

        return new PagedResultDto<NotificationListItemDto>
        {
            Items = page,
            TotalCount = totalCount
        };
    }

    public async Task<int> CountPendingDeliveriesByTenantAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty)
        {
            return 0;
        }

        // Internal (M2M) callers are in host scope: read the tenant's own database, with the tenant filter lifted so
        // the explicit TenantId predicate selects its rows in the shared database too.
        using var tenantDatabase = _tenantDatabases.EnterDatabase(await FindHostTargetTenantDatabaseAsync(tenantId, cancellationToken));
        using var tenantFilterScope = BeginTenantFilterScope(tenantId);
        var notifications = await _notificationRepository.GetQueryableAsync();
        var deliveries = await _deliveryRepository.GetQueryableAsync();
        return await deliveries
            .Join(
                notifications,
                delivery => delivery.NotificationId,
                notification => notification.Id,
                (delivery, notification) => new { delivery, notification })
            .Where(x => x.notification.TenantId == tenantId &&
                        (x.delivery.Status == NotificationStatus.Queued ||
                         x.delivery.Status == NotificationStatus.Processing))
            .CountAsync(cancellationToken);
    }

    public async Task<NotificationDto> GetAsync(Guid id)
    {
        await EnsurePermissionAsync(NotificationsPermissions.Query.Read);
        using var owningDatabase = _tenantDatabases.EnterDatabase(await FindHostNotificationDatabaseAsync(id));
        using var tenantFilterScope = BeginEntityLookupScope();
        var notification = await FindNotificationAsync(id);
        EnsureTenantAccess(notification.TenantId);
        return await ProjectDetailAsync(notification);
    }

    public async Task<List<NotificationDeliveryDto>> GetDeliveriesAsync(Guid notificationId)
    {
        await EnsurePermissionAsync(NotificationsPermissions.Query.Read);
        using var owningDatabase = _tenantDatabases.EnterDatabase(await FindHostNotificationDatabaseAsync(notificationId));
        using var tenantFilterScope = BeginEntityLookupScope();
        var notification = await FindNotificationAsync(notificationId);
        EnsureTenantAccess(notification.TenantId);

        var deliveries = await _deliveryRepository.WithDetailsAsync(x => x.Recipient);

        return await deliveries
            .Where(x => x.NotificationId == notificationId && x.TenantId == notification.TenantId)
            .AsNoTracking()
            .OrderBy(x => x.CreationTime)
            .ThenBy(x => x.Id)
            .Select(delivery => new NotificationDeliveryDto
            {
                Id = delivery.Id,
                NotificationId = delivery.NotificationId,
                RecipientId = delivery.RecipientId,
                RecipientType = delivery.Recipient.RecipientType,
                RecipientValue = _querySecurityOptions.IncludeRecipientValue
                    ? delivery.Recipient.RecipientValue
                    : string.Empty,
                Status = delivery.Status,
                ProviderMessageId = delivery.ProviderMessageId,
                LastError = _querySecurityOptions.IncludeLastError ? delivery.LastError : null,
                TryCount = delivery.TryCount,
                LastTryTimeUtc = delivery.LastTryTimeUtc,
                SentTimeUtc = delivery.SentTimeUtc,
                NextAttemptTimeUtc = delivery.NextAttemptTimeUtc,
                NotBeforeUtc = delivery.NotBeforeUtc,
                PolicyHeldUntilUtc = delivery.PolicyHeldUntilUtc,
                DeliveryAttemptId = delivery.DeliveryAttemptId,
                DeliveryAttemptedAtUtc = delivery.DeliveryAttemptedAtUtc,
                CreationTime = delivery.CreationTime
            })
            .ToListAsync();
    }

    public async Task<PagedResultDto<NotificationDeliveryChronologyItemDto>> GetDeliveryChronologyAsync(
        long deliveryId,
        GetNotificationDeliveryChronologyInput input,
        CancellationToken cancellationToken = default)
    {
        await EnsurePermissionAsync(NotificationsPermissions.Query.Read);
        using var owningDatabase = _tenantDatabases.EnterDatabase(_currentTenant.Id.HasValue
            ? null
            : await _databaseLocator.FindDeliveryDatabaseAsync(deliveryId, input.TenantId, cancellationToken));
        using var tenantFilterScope = BeginEntityLookupScope();
        var delivery = await FindDeliveryAsync(deliveryId, cancellationToken);
        if (input.TenantId is { } requestedTenantId && delivery.TenantId != requestedTenantId)
        {
            throw new EntityNotFoundException(NotificationErrorCodes.DeliveryNotFound, "Notification delivery not found.");
        }

        EnsureTenantAccess(delivery.TenantId);
        return await GetDeliveryChronologyPageAsync(delivery, input, cancellationToken);
    }

    public Task<PagedResultDto<NotificationListItemDto>> GetCurrentTenantListAsync(GetNotificationsInput input)
    {
        input.TenantId = RequireCurrentTenantId();
        return GetListAsync(input);
    }

    public async Task<NotificationDto> GetCurrentTenantAsync(Guid id)
    {
        await EnsurePermissionAsync(NotificationsPermissions.Query.Read);
        var currentTenantId = RequireCurrentTenantId();
        var notification = await FindNotificationAsync(id);
        EnsureCurrentTenant(notification.TenantId, currentTenantId);
        return await ProjectDetailAsync(notification);
    }

    public async Task<List<NotificationDeliveryDto>> GetCurrentTenantDeliveriesAsync(Guid notificationId)
    {
        await EnsurePermissionAsync(NotificationsPermissions.Query.Read);
        var currentTenantId = RequireCurrentTenantId();
        var notification = await FindNotificationAsync(notificationId);
        EnsureCurrentTenant(notification.TenantId, currentTenantId);
        return await GetDeliveriesAsync(notificationId);
    }

    public async Task<PagedResultDto<NotificationDeliveryChronologyItemDto>> GetCurrentTenantDeliveryChronologyAsync(
        long deliveryId,
        GetNotificationDeliveryChronologyInput input,
        CancellationToken cancellationToken = default)
    {
        await EnsurePermissionAsync(NotificationsPermissions.Query.Read);
        var currentTenantId = RequireCurrentTenantId();
        var delivery = await FindDeliveryAsync(deliveryId, cancellationToken);
        EnsureCurrentTenant(delivery.TenantId, currentTenantId);
        return await GetDeliveryChronologyPageAsync(delivery, input, cancellationToken);
    }

    public async Task<List<NotificationRecoverySummaryDto>> GetCurrentTenantRecoveryAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsurePermissionAsync(NotificationsPermissions.Query.Read);
        var currentTenantId = RequireCurrentTenantId();
        var notifications = await _notificationRepository.GetQueryableAsync();
        var deliveries = await _deliveryRepository.GetQueryableAsync();

        return await deliveries
            .AsNoTracking()
            .Join(
                notifications.AsNoTracking(),
                delivery => delivery.NotificationId,
                notification => notification.Id,
                (delivery, notification) => new { delivery, notification })
            .Where(row =>
                row.notification.TenantId == currentTenantId &&
                (row.delivery.Status == NotificationStatus.Failed ||
                 row.delivery.Status == NotificationStatus.NeedsReview ||
                 row.delivery.Status == NotificationStatus.DeadLettered))
            .GroupBy(row => new
            {
                row.notification.SourceService,
                row.notification.Category,
                row.notification.ActionUrl,
                row.delivery.Status
            })
            .OrderBy(group => group.Key.SourceService)
            .ThenBy(group => group.Key.Status)
            .Take(100)
            .Select(group => new NotificationRecoverySummaryDto
            {
                SourceService = group.Key.SourceService,
                Category = group.Key.Category,
                ActionUrl = group.Key.ActionUrl,
                Status = group.Key.Status,
                Count = group.Count(),
                OldestAtUtc = group.Min(row => row.delivery.CreationTime),
                LatestAtUtc = group.Max(row => row.delivery.LastTryTimeUtc ?? row.delivery.CreationTime),
                SafeReason = group.Key.Status == NotificationStatus.NeedsReview
                    ? "Delivery outcome requires an authorized operator decision."
                    : group.Key.Status == NotificationStatus.DeadLettered
                        ? "Automatic delivery attempts are exhausted."
                        : "Delivery failed and may be retried when the dependency is available.",
                CanRetry = group.Key.Status != NotificationStatus.NeedsReview
            })
            .ToListAsync(cancellationToken);
    }

    private async Task<Notification> FindNotificationAsync(Guid id)
    {
        var notification = await _notificationRepository.FindAsync(x => x.Id == id, includeDetails: false);
        return notification ?? throw new EntityNotFoundException(
            NotificationErrorCodes.NotificationNotFound,
            "Notification not found.");
    }

    private async Task<NotificationDelivery> FindDeliveryAsync(
        long deliveryId,
        CancellationToken cancellationToken)
    {
        if (deliveryId <= 0)
        {
            throw new SharedValidationException(
                NotificationErrorCodes.DeliveryIdRequired,
                "DeliveryId is required.");
        }

        var deliveries = await _deliveryRepository.GetQueryableAsync();
        return await deliveries
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == deliveryId, cancellationToken)
            ?? throw new EntityNotFoundException(
                NotificationErrorCodes.DeliveryNotFound,
                "Notification delivery not found.");
    }

    private async Task<NotificationDto> ProjectDetailAsync(Notification notification)
    {
        var principal = _currentPrincipalAccessor.Principal;
        var canReadSensitivePayload = await _permissionChecker.IsGrantedAsync(
            principal,
            NotificationsPermissions.Query.ReadSensitivePayload);

        if (canReadSensitivePayload)
        {
            await PublishSensitivePayloadReadAuditAsync(notification, principal);
        }

        return new NotificationDto
        {
            Id = notification.Id,
            RequestId = notification.RequestId,
            TenantId = notification.TenantId,
            Channel = notification.Channel,
            TemplateName = notification.TemplateName,
            SourceService = notification.SourceService,
            TemplateModelJson = canReadSensitivePayload ? notification.TemplateModelJson : string.Empty,
            Redaction = new NotificationRedactionMetadataDto
            {
                RedactionApplied = !canReadSensitivePayload,
                WithheldFieldCount = canReadSensitivePayload ? 0 : 1,
                WithheldFields = canReadSensitivePayload ? [] : [TemplateModelFieldName]
            },
            Subject = notification.Subject,
            Category = notification.Category,
            Priority = notification.Priority,
            ActionUrl = notification.ActionUrl,
            CreationTime = notification.CreationTime,
            ScheduledForUtc = notification.ScheduledForUtc
        };
    }

    private async Task<PagedResultDto<NotificationDeliveryChronologyItemDto>> GetDeliveryChronologyPageAsync(
        NotificationDelivery delivery,
        GetNotificationDeliveryChronologyInput input,
        CancellationToken cancellationToken)
    {
        var chronology = await _chronologyRepository.GetQueryableAsync();
        var query = chronology
            .AsNoTracking()
            .Where(x => x.DeliveryId == delivery.Id && x.TenantId == delivery.TenantId);

        var totalCount = await query.LongCountAsync(cancellationToken);
        var skip = Math.Max(0, input.SkipCount);
        var take = input.MaxResultCount <= 0
            ? DefaultChronologyPageSize
            : Math.Min(input.MaxResultCount, MaxChronologyPageSize);

        var items = await query
            .OrderBy(x => x.OccurredAtUtc)
            .ThenBy(x => x.Id)
            .Skip(skip)
            .Take(take)
            .Select(x => new NotificationDeliveryChronologyItemDto
            {
                Id = x.Id,
                DeliveryId = x.DeliveryId,
                EventKind = x.EventKind,
                Status = x.Status,
                ProviderMessageId = x.ProviderMessageId,
                ResponseCode = x.ResponseCode,
                SafeError = x.SafeError,
                LatencyMs = x.LatencyMs,
                ActorId = x.ActorId,
                ActorType = x.ActorType,
                OccurredAtUtc = x.OccurredAtUtc
            })
            .ToListAsync(cancellationToken);

        return new PagedResultDto<NotificationDeliveryChronologyItemDto>
        {
            Items = items,
            TotalCount = totalCount
        };
    }

    private async Task PublishSensitivePayloadReadAuditAsync(
        Notification notification,
        ClaimsPrincipal principal)
    {
        var actorId = principal.FindFirstValue(SharedClaimTypes.Subject)
            ?? principal.FindFirstValue(SharedClaimTypes.UserId);

        await _auditLogPublisher.PublishAsync(
            new AuditLogWriteRequest
            {
                EventId = Guid.CreateVersion7(),
                TenantId = notification.TenantId,
                OccurredAtUtc = DateTime.UtcNow,
                SourceService = "notifications-service",
                Category = "notifications.sensitive-payload.read",
                UserId = string.IsNullOrWhiteSpace(actorId) ? null : actorId.Trim(),
                StatusCode = StatusCodes.Status200OK,
                RedactionApplied = true,
                RedactionVersion = 1,
                Actions =
                [
                    new AuditLogWriteRequestAction
                    {
                        Name = "notifications.query.read-sensitive-payload",
                        Result = "disclosed",
                        Parameters = new Dictionary<string, string?>(StringComparer.Ordinal)
                        {
                            ["notificationId"] = notification.Id.ToString("D"),
                            ["tenantScope"] = notification.TenantId?.ToString("D") ?? "host",
                            ["fieldCount"] = 1.ToString(CultureInfo.InvariantCulture)
                        }
                    }
                ]
            });
    }

    private Guid? ResolveTenantScope(Guid? requestedTenantId)
    {
        if (!_currentTenant.Id.HasValue)
        {
            return requestedTenantId;
        }

        if (requestedTenantId.HasValue && requestedTenantId.Value != _currentTenant.Id.Value)
        {
            throw new UnauthorizedAccessException("Notification queries must match the authenticated tenant.");
        }

        return _currentTenant.Id.Value;
    }

    /// <summary>
    /// The database host work addressing one tenant must enter (see
    /// <see cref="TenantDatabaseFanOut.FindTenantDatabaseAsync{TDbContext}"/>); tenant callers are already there.
    /// </summary>
    private async Task<TenantDatabase?> FindHostTargetTenantDatabaseAsync(
        Guid? tenantId,
        CancellationToken cancellationToken = default) =>
        !_currentTenant.Id.HasValue && tenantId.HasValue
            ? await _tenantDatabases.FindTenantDatabaseAsync<NotificationsDbContextApp>(tenantId.Value, cancellationToken)
            : null;

    /// <summary>Host lookups by notification id run in the database that stores the notification.</summary>
    private async Task<TenantDatabase?> FindHostNotificationDatabaseAsync(Guid notificationId) =>
        _currentTenant.Id.HasValue
            ? null
            : await _databaseLocator.FindNotificationDatabaseAsync(notificationId);

    // Lifts the tenant filter for a host read that names one tenant; it runs inside that tenant's database scope
    // (FindHostTargetTenantDatabaseAsync), so it is a no-op once a dedicated database put the call in tenant scope.
    private IDisposable? BeginTenantFilterScope(Guid? tenantId)
    {
        return !_currentTenant.Id.HasValue && tenantId.HasValue
            ? _dataFilter.Disable<IMultiTenant>()
            : null;
    }

    // Lifts the tenant filter for a host lookup by id; it runs inside the owning database's scope (the locator), so it
    // is a no-op once a dedicated database put the call in tenant scope.
    private IDisposable? BeginEntityLookupScope()
    {
        return !_currentTenant.Id.HasValue
            ? _dataFilter.Disable<IMultiTenant>()
            : null;
    }

    private void EnsureTenantAccess(Guid? tenantId)
    {
        if (_currentTenant.Id.HasValue && tenantId != _currentTenant.Id.Value)
        {
            throw new UnauthorizedAccessException("Cross-tenant notification queries are not allowed.");
        }
    }

    private Guid RequireCurrentTenantId()
    {
        return _currentTenant.Id
            ?? throw new UnauthorizedAccessException("Tenant notification queries require an active tenant context.");
    }

    private static void EnsureCurrentTenant(Guid? tenantId, Guid currentTenantId)
    {
        if (tenantId != currentTenantId)
        {
            throw new UnauthorizedAccessException("Cross-tenant notification queries are not allowed.");
        }
    }

    private async Task EnsurePermissionAsync(string permission)
    {
        if (!await _permissionChecker.IsGrantedAsync(_currentPrincipalAccessor.Principal, permission))
        {
            throw new SharedForbiddenException(
                "notifications_permission_required",
                "The exact notification permission is required.");
        }
    }
}
