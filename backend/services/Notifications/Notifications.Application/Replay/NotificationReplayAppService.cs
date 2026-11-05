using Notifications.Application.Dispatches;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Notifications.Application.Contracts.Delivery;
using Notifications.Application.Delivery;
using Notifications.Application.Contracts.Replay;
using Notifications.Domain.Notifications;
using Notifications.Domain.Shared.Constants;
using Notifications.Domain.Shared.Enums;
using Shared;
using Shared.Data.Filtering;
using Shared.Domain;
using Shared.Domain.Entities;
using Shared.Domain.Repositories;
using Shared.EntityFrameworkCore.TenantDatabases;
using Shared.EventContracts;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;

namespace Notifications.Application.Replay;

public sealed class NotificationReplayAppService : INotificationReplayAppService, ITransientDependency
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IRepository<Notification, Guid> _notificationRepository;
    private readonly IRepository<NotificationRecipient, long> _recipientRepository;
    private readonly INotificationDeliveryScheduler _notificationDeliveryScheduler;
    private readonly IDataFilter _dataFilter;
    private readonly ICurrentTenant _currentTenant;
    private readonly NotificationDatabaseLocator _databaseLocator;

    public NotificationReplayAppService(
        IRepository<Notification, Guid> notificationRepository,
        IRepository<NotificationRecipient, long> recipientRepository,
        INotificationDeliveryScheduler notificationDeliveryScheduler,
        IDataFilter dataFilter,
        ICurrentTenant currentTenant,
        NotificationDatabaseLocator databaseLocator)
    {
        _notificationRepository = notificationRepository;
        _recipientRepository = recipientRepository;
        _notificationDeliveryScheduler = notificationDeliveryScheduler;
        _dataFilter = dataFilter;
        _currentTenant = currentTenant;
        _databaseLocator = databaseLocator;
    }

    public async Task<ReplayNotificationResultDto> ReplayAsync(
        ReplayNotificationInput input,
        CancellationToken cancellationToken = default)
    {
        if (input.NotificationId == Guid.Empty)
        {
            throw new SharedValidationException(
                NotificationErrorCodes.NotificationIdRequired,
                "NotificationId is required.");
        }

        EnsureOperationId(input.OperationId);
        using var owningDatabase = _databaseLocator.Enter(await FindHostNotificationDatabaseAsync(input.NotificationId, cancellationToken));
        using var tenantFilterScope = BeginEntityLookupScope();

        var notificationQueryable = await _notificationRepository.GetQueryableAsync();
        var notification = await notificationQueryable
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == input.NotificationId, cancellationToken);

        if (notification == null)
        {
            throw new EntityNotFoundException(
                NotificationErrorCodes.NotificationNotFound,
                $"Notification '{input.NotificationId}' was not found.");
        }

        EnsureTenantAccess(notification.TenantId);
        await NotificationDispatchRecoveryGate.RejectDetachedCloneAsync(_notificationRepository, notification, cancellationToken);

        var recipientQueryable = await _recipientRepository.GetQueryableAsync();
        var recipients = await recipientQueryable
            .Where(x =>
                x.NotificationId == input.NotificationId &&
                x.TenantId == notification.TenantId)
            .AsNoTracking()
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);

        if (recipients.Count == 0)
        {
            throw new SharedConflictException(
                NotificationErrorCodes.NoReplayRecipients,
                "The selected notification has no resolved recipients to replay.");
        }

        Dictionary<string, string?> model;
        try
        {
            model = string.IsNullOrWhiteSpace(notification.TemplateModelJson)
                ? new Dictionary<string, string?>(StringComparer.Ordinal)
                : JsonSerializer.Deserialize<Dictionary<string, string?>>(notification.TemplateModelJson, JsonOptions)
                    ?? new Dictionary<string, string?>(StringComparer.Ordinal);
        }
        catch (JsonException exception)
        {
            throw new SharedConflictException(
                NotificationErrorCodes.TemplateModelInvalid,
                "The selected notification template model is invalid and cannot be replayed.",
                exception);
        }

        var replayTemplate = new NotificationChannelRequestHost
        {
            RequestId = input.OperationId,
            Channel = MapChannel(notification.Channel),
            Subject = notification.Subject,
            TemplateName = notification.TemplateName,
            TemplateModel = model,
            Recipients = recipients
                .Select(recipient => new NotificationRecipientEto
                {
                    RecipientType = MapRecipientType(recipient.RecipientType),
                    RecipientValue = recipient.RecipientValue
                })
                .ToList(),
            OccurredAtUtc = DateTime.UtcNow,
            SourceService = "Notifications.AdminReplay",
            RespectUserPreferences = false,
            PreferenceUserId = null,
            PreferenceKey = null
        };
        var scheduleResult = input.ScheduledForUtc.HasValue
            ? await NotificationContractFactory.ScheduleAtAsync(
                _notificationDeliveryScheduler,
                notification.TenantId,
                replayTemplate,
                input.ScheduledForUtc.Value,
                cancellationToken)
            : await NotificationContractFactory.ScheduleAsync(
                _notificationDeliveryScheduler,
                notification.TenantId,
                replayTemplate,
                cancellationToken);

        return new ReplayNotificationResultDto
        {
            Accepted = scheduleResult.Accepted,
            IsDuplicate = scheduleResult.IsDuplicate,
            SkippedByPreferences = scheduleResult.SkippedByPreferences,
            RequestId = scheduleResult.RequestId,
            NotificationId = scheduleResult.NotificationId,
            OriginalNotificationId = notification.Id,
            QueuedCount = scheduleResult.QueuedCount,
            RecipientCount = recipients.Count,
            ScheduledForUtc = scheduleResult.ScheduledForUtc
        };
    }

    public async Task<ReplayNotificationOverridePreviewDto> PreviewOverrideAsync(
        PreviewReplayNotificationOverrideInput input,
        CancellationToken cancellationToken = default)
    {
        using var owningDatabase = _databaseLocator.Enter(await FindHostNotificationDatabaseAsync(input.NotificationId, cancellationToken));
        using var tenantFilterScope = BeginEntityLookupScope();
        var notification = await GetNotificationAsync(input.NotificationId, cancellationToken);
        EnsureTenantAccess(notification.TenantId);
        await NotificationDispatchRecoveryGate.RejectDetachedCloneAsync(_notificationRepository, notification, cancellationToken);
        var recipients = await LoadSelectedRecipientsAsync(
            notification,
            input.RecipientIds,
            cancellationToken);

        return new ReplayNotificationOverridePreviewDto
        {
            NotificationId = notification.Id,
            RequestId = notification.RequestId,
            TenantId = notification.TenantId,
            Channel = notification.Channel,
            TemplateName = notification.TemplateName,
            Subject = notification.Subject,
            RecipientCount = recipients.Count,
            Recipients = recipients
                .Select(recipient => new NotificationReplayRecipientOptionDto
                {
                    RecipientId = recipient.Id,
                    RecipientType = recipient.RecipientType,
                    DisplayLabel = MaskRecipient(recipient)
                })
                .ToList(),
            CanExecute = true,
            Warning = "Targeted replay preserves the original tenant, channel, template, subject, and model and can only select original recipients."
        };
    }

    public async Task<ReplayNotificationResultDto> ReplayOverrideAsync(
        ReplayNotificationOverrideInput input,
        CancellationToken cancellationToken = default)
    {
        using var owningDatabase = _databaseLocator.Enter(await FindHostNotificationDatabaseAsync(input.NotificationId, cancellationToken));
        using var tenantFilterScope = BeginEntityLookupScope();
        var notification = await GetNotificationAsync(input.NotificationId, cancellationToken);
        EnsureTenantAccess(notification.TenantId);
        await NotificationDispatchRecoveryGate.RejectDetachedCloneAsync(_notificationRepository, notification, cancellationToken);
        EnsureOperationId(input.OperationId);
        var recipients = await LoadSelectedRecipientsAsync(
            notification,
            input.RecipientIds,
            cancellationToken);

        var model = DeserializeTemplateModel(notification, "replayed");
        var replayTemplate = new NotificationChannelRequestHost
        {
            RequestId = input.OperationId,
            Channel = MapChannel(notification.Channel),
            Subject = notification.Subject,
            TemplateName = notification.TemplateName,
            TemplateModel = model,
            Recipients = recipients
                .Select(recipient => new NotificationRecipientEto
                {
                    RecipientType = MapRecipientType(recipient.RecipientType),
                    RecipientValue = recipient.RecipientValue
                })
                .ToList(),
            OccurredAtUtc = DateTime.UtcNow,
            SourceService = "Notifications.AdminReplayOverride",
            RespectUserPreferences = false,
            PreferenceUserId = null,
            PreferenceKey = null
        };
        var scheduleResult = input.ScheduledForUtc.HasValue
            ? await NotificationContractFactory.ScheduleAtAsync(
                _notificationDeliveryScheduler,
                notification.TenantId,
                replayTemplate,
                input.ScheduledForUtc.Value,
                cancellationToken)
            : await NotificationContractFactory.ScheduleAsync(
                _notificationDeliveryScheduler,
                notification.TenantId,
                replayTemplate,
                cancellationToken);

        return new ReplayNotificationResultDto
        {
            Accepted = scheduleResult.Accepted,
            IsDuplicate = scheduleResult.IsDuplicate,
            SkippedByPreferences = scheduleResult.SkippedByPreferences,
            RequestId = scheduleResult.RequestId,
            NotificationId = scheduleResult.NotificationId,
            OriginalNotificationId = notification.Id,
            QueuedCount = scheduleResult.QueuedCount,
            RecipientCount = recipients.Count,
            ScheduledForUtc = scheduleResult.ScheduledForUtc
        };
    }

    private async Task<Notification> GetNotificationAsync(Guid notificationId, CancellationToken cancellationToken)
    {
        if (notificationId == Guid.Empty)
        {
            throw new SharedValidationException(
                NotificationErrorCodes.NotificationIdRequired,
                "NotificationId is required.");
        }

        var notificationQueryable = await _notificationRepository.GetQueryableAsync();
        var notification = await notificationQueryable
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == notificationId, cancellationToken);

        return notification ?? throw new EntityNotFoundException(
            NotificationErrorCodes.NotificationNotFound,
            $"Notification '{notificationId}' was not found.");
    }

    /// <summary>
    /// Host replays read the original and schedule the replay in the database that stores the notification, so a
    /// dedicated tenant's replay is never written to the shared database. Tenant callers are already there.
    /// </summary>
    private async Task<TenantDatabase?> FindHostNotificationDatabaseAsync(
        Guid notificationId,
        CancellationToken cancellationToken) =>
        _currentTenant.Id.HasValue
            ? null
            : await _databaseLocator.FindNotificationDatabaseAsync(notificationId, cancellationToken);

    // Lifts the tenant filter for a host lookup by id inside the owning database's scope; a no-op once a dedicated
    // database put the call in tenant scope.
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
            throw new UnauthorizedAccessException("Cross-tenant notification replay is not allowed.");
        }
    }

    private static Dictionary<string, string?> DeserializeTemplateModel(Notification notification, string operationName)
    {
        try
        {
            return string.IsNullOrWhiteSpace(notification.TemplateModelJson)
                ? new Dictionary<string, string?>(StringComparer.Ordinal)
                : JsonSerializer.Deserialize<Dictionary<string, string?>>(notification.TemplateModelJson, JsonOptions)
                    ?? new Dictionary<string, string?>(StringComparer.Ordinal);
        }
        catch (JsonException exception)
        {
            throw new SharedConflictException(
                NotificationErrorCodes.TemplateModelInvalid,
                $"The selected notification template model is invalid and cannot be {operationName}.",
                exception);
        }
    }

    private async Task<List<NotificationRecipient>> LoadSelectedRecipientsAsync(
        Notification notification,
        IEnumerable<long>? recipientIds,
        CancellationToken cancellationToken)
    {
        var selectedIds = (recipientIds ?? [])
            .Where(id => id > 0)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();

        if (selectedIds.Length == 0)
        {
            throw new SharedValidationException(
                NotificationErrorCodes.OverrideRecipientsRequired,
                "At least one original recipient must be selected.");
        }

        var recipientQueryable = await _recipientRepository.GetQueryableAsync();
        var recipients = await recipientQueryable
            .AsNoTracking()
            .Where(recipient =>
                recipient.NotificationId == notification.Id &&
                recipient.TenantId == notification.TenantId &&
                selectedIds.Contains(recipient.Id))
            .OrderBy(recipient => recipient.Id)
            .ToListAsync(cancellationToken);

        if (recipients.Count != selectedIds.Length)
        {
            throw new SharedValidationException(
                NotificationErrorCodes.OverrideRecipientsRequired,
                "Every selected recipient must belong to the original notification.");
        }

        return recipients;
    }

    private static void EnsureOperationId(Guid operationId)
    {
        if (operationId == Guid.Empty)
        {
            throw new SharedValidationException(
                NotificationErrorCodes.ReplayOperationIdRequired,
                "OperationId is required.");
        }
    }

    private static string MaskRecipient(NotificationRecipient recipient)
    {
        var value = recipient.RecipientValue.Trim();
        return recipient.RecipientType switch
        {
            NotificationRecipientType.EmailAddress => MaskEmail(value),
            NotificationRecipientType.PhoneNumber => value.Length <= 4
                ? new string('•', value.Length)
                : $"••••{value[^4..]}",
            NotificationRecipientType.UserId => "User recipient",
            NotificationRecipientType.DeviceToken => "Registered device",
            _ => "Original recipient"
        };
    }

    private static string MaskEmail(string value)
    {
        var separator = value.LastIndexOf('@');
        if (separator <= 0 || separator == value.Length - 1)
        {
            return "Email recipient";
        }

        return $"{value[0]}•••@{value[(separator + 1)..]}";
    }

    private static NotificationSendChannel MapChannel(NotificationChannel channel)
    {
        return channel switch
        {
            NotificationChannel.Email => NotificationSendChannel.Email,
            NotificationChannel.Sms => NotificationSendChannel.Sms,
            NotificationChannel.Push => NotificationSendChannel.Push,
            NotificationChannel.App => NotificationSendChannel.App,
            NotificationChannel.WhatsApp => NotificationSendChannel.WhatsApp,
            _ => throw new NotSupportedException($"Unsupported notification channel: {channel}")
        };
    }

    private static NotificationSendRecipientType MapRecipientType(NotificationRecipientType recipientType)
    {
        return recipientType switch
        {
            NotificationRecipientType.EmailAddress => NotificationSendRecipientType.EmailAddress,
            NotificationRecipientType.PhoneNumber => NotificationSendRecipientType.PhoneNumber,
            NotificationRecipientType.UserId => NotificationSendRecipientType.UserId,
            NotificationRecipientType.DeviceToken => NotificationSendRecipientType.DeviceToken,
            _ => throw new NotSupportedException($"Unsupported notification recipient type: {recipientType}")
        };
    }
}
