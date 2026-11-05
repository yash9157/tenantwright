using Notifications.Application.Dispatches;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text.Json;
using Notifications.Application.Contracts.Delivery;
using Notifications.Application.Delivery;
using Notifications.Application.Contracts.Notifications;
using Notifications.Application.Contracts.Replay;
using Notifications.Domain.Notifications;
using Notifications.Domain.Shared.Constants;
using Notifications.Domain.Shared.Enums;
using Shared;
using Shared.Domain.Entities;
using Shared.Domain.Repositories;
using Shared.EventContracts;
using Shared.Modularity.DependencyInjection;
using Shared.Security;
using Shared.Permissions.Authorization;
using Shared.Permissions.Definitions.Notifications;

namespace Notifications.Application.Notifications;

public sealed class NotificationDeliveryOperationsAppService : INotificationDeliveryOperationsAppService, ITransientDependency
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IRepository<Notification, Guid> _notificationRepository;
    private readonly IRepository<NotificationDelivery, long> _deliveryRepository;
    private readonly INotificationDeliveryScheduler _deliveryScheduler;
    private readonly ICurrentPrincipalAccessor _currentPrincipalAccessor;
    private readonly IPermissionChecker _permissionChecker;
    private readonly TimeProvider _timeProvider;

    public NotificationDeliveryOperationsAppService(
        IRepository<Notification, Guid> notificationRepository,
        IRepository<NotificationDelivery, long> deliveryRepository,
        INotificationDeliveryScheduler deliveryScheduler,
        ICurrentPrincipalAccessor currentPrincipalAccessor,
        IPermissionChecker permissionChecker,
        TimeProvider timeProvider)
    {
        _notificationRepository = notificationRepository;
        _deliveryRepository = deliveryRepository;
        _deliveryScheduler = deliveryScheduler;
        _currentPrincipalAccessor = currentPrincipalAccessor;
        _permissionChecker = permissionChecker;
        _timeProvider = timeProvider;
    }

    public async Task<ResendNotificationDeliveryResultDto> ResendDeliveryAsync(
        ResendNotificationDeliveryInput input,
        CancellationToken cancellationToken = default)
    {
        await EnsurePermissionAsync(NotificationsPermissions.Deliveries.Resend);
        if (input.DeliveryId <= 0)
        {
            throw new SharedValidationException(
                NotificationErrorCodes.DeliveryIdRequired,
                "DeliveryId is required.");
        }

        var deliveryQueryable = await _deliveryRepository.WithDetailsAsync(x => x.Notification, x => x.Recipient);
        var delivery = await deliveryQueryable
            .FirstOrDefaultAsync(x => x.Id == input.DeliveryId, cancellationToken);

        if (delivery == null)
        {
            throw new EntityNotFoundException(
                NotificationErrorCodes.DeliveryNotFound,
                $"Delivery '{input.DeliveryId}' was not found.");
        }

        if (delivery.Status != NotificationStatus.Failed)
        {
            throw new SharedConflictException(
                NotificationErrorCodes.DeliveryNotFailed,
                "Only failed delivery rows can be resent.");
        }

        return await ScheduleResendAsync(
            delivery.Notification,
            [delivery],
            failedDeliveryCount: 1,
            cancellationToken);
    }

    public async Task<ResendNotificationDeliveryResultDto> ResendFailedAsync(
        ResendFailedNotificationDeliveriesInput input,
        CancellationToken cancellationToken = default)
    {
        await EnsurePermissionAsync(NotificationsPermissions.Deliveries.Resend);
        if (input.NotificationId == Guid.Empty)
        {
            throw new SharedValidationException(
                NotificationErrorCodes.NotificationIdRequired,
                "NotificationId is required.");
        }

        var notificationQueryable = await _notificationRepository.GetQueryableAsync();
        var notification = await notificationQueryable
            .FirstOrDefaultAsync(x => x.Id == input.NotificationId, cancellationToken);

        if (notification == null)
        {
            throw new EntityNotFoundException(
                NotificationErrorCodes.NotificationNotFound,
                $"Notification '{input.NotificationId}' was not found.");
        }

        var deliveryQueryable = await _deliveryRepository.WithDetailsAsync(x => x.Recipient);
        var failedDeliveries = await deliveryQueryable
            .Where(x => x.NotificationId == input.NotificationId && x.Status == NotificationStatus.Failed)
            .OrderBy(x => x.CreationTime)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);

        if (failedDeliveries.Count == 0)
        {
            throw new SharedConflictException(
                NotificationErrorCodes.NoFailedDeliveries,
                "The selected notification has no failed deliveries to resend.");
        }

        return await ScheduleResendAsync(
            notification,
            failedDeliveries,
            failedDeliveries.Count,
            cancellationToken);
    }

    public async Task<NotificationDeliveryOverridePreviewDto> PreviewOverrideResendAsync(
        PreviewNotificationDeliveryOverrideResendInput input,
        CancellationToken cancellationToken = default)
    {
        await EnsurePermissionAsync(NotificationsPermissions.Deliveries.OverrideResend);
        var delivery = await GetFailedDeliveryAsync(input.DeliveryId, cancellationToken);
        await NotificationDispatchRecoveryGate.RejectDetachedCloneAsync(_notificationRepository, delivery.Notification, cancellationToken);
        var recipients = NormalizeOverrideRecipients(input.Recipients);

        return new NotificationDeliveryOverridePreviewDto
        {
            DeliveryId = delivery.Id,
            NotificationId = delivery.NotificationId,
            TenantId = delivery.Notification.TenantId,
            Channel = delivery.Notification.Channel,
            TemplateName = delivery.Notification.TemplateName,
            Subject = delivery.Notification.Subject,
            OriginalRecipientType = delivery.Recipient.RecipientType,
            OriginalRecipientValue = delivery.Recipient.RecipientValue,
            RecipientCount = recipients.Count,
            Recipients = recipients,
            CanExecute = true,
            Warning = "Override resend preserves the original tenant, channel, template, subject, and model; only recipients are replaced."
        };
    }

    public async Task<ResendNotificationDeliveryResultDto> ResendOverrideAsync(
        ResendNotificationDeliveryOverrideInput input,
        CancellationToken cancellationToken = default)
    {
        await EnsurePermissionAsync(NotificationsPermissions.Deliveries.OverrideResend);
        var delivery = await GetFailedDeliveryAsync(input.DeliveryId, cancellationToken);
        await NotificationDispatchRecoveryGate.RejectDetachedCloneAsync(_notificationRepository, delivery.Notification, cancellationToken);
        var recipients = NormalizeOverrideRecipients(input.Recipients);

        var model = DeserializeTemplateModel(delivery.Notification);
        var scheduleResult = await NotificationContractFactory.ScheduleAsync(
            _deliveryScheduler,
                delivery.Notification.TenantId,
                new NotificationChannelRequestHost
                {
                    RequestId = Guid.CreateVersion7(),
                    Channel = MapChannel(delivery.Notification.Channel),
                    Subject = delivery.Notification.Subject,
                    TemplateName = delivery.Notification.TemplateName,
                    TemplateModel = model,
                    Recipients = recipients
                        .Select(recipient => new NotificationRecipientEto
                        {
                            RecipientType = MapRecipientType(recipient.RecipientType),
                            RecipientValue = recipient.RecipientValue
                        })
                        .ToList(),
                    OccurredAtUtc = _timeProvider.GetUtcNow().UtcDateTime,
                    SourceService = "Notifications.AdminDeliveryOverrideResend",
                    RespectUserPreferences = false,
                    PreferenceUserId = null,
                    PreferenceKey = null
                },
            cancellationToken);

        delivery.RecordResendRequested(_timeProvider.GetUtcNow().UtcDateTime, GetCurrentActorId(), recipientOverride: true);
        await _deliveryRepository.UpdateAsync(delivery, autoSave: true, cancellationToken);

        return new ResendNotificationDeliveryResultDto
        {
            Accepted = scheduleResult.Accepted,
            IsDuplicate = scheduleResult.IsDuplicate,
            SkippedByPreferences = scheduleResult.SkippedByPreferences,
            RequestId = scheduleResult.RequestId,
            NotificationId = scheduleResult.NotificationId,
            OriginalNotificationId = delivery.Notification.Id,
            QueuedCount = scheduleResult.QueuedCount,
            RecipientCount = recipients.Count,
            FailedDeliveryCount = 1
        };
    }

    public async Task<ReconcileNotificationDeliveryResultDto> ConfirmDeliveryAsync(
        ConfirmNotificationDeliveryInput input,
        CancellationToken cancellationToken = default)
    {
        await EnsurePermissionAsync(NotificationsPermissions.Deliveries.Resend);
        var delivery = await GetReviewDeliveryAsync(input.DeliveryId, cancellationToken);
        if (delivery.Status != NotificationStatus.NeedsReview)
        {
            throw new SharedConflictException(
                NotificationErrorCodes.DeliveryNotConfirmable,
                "Only review-required delivery rows can be confirmed.");
        }

        var actorId = GetCurrentActorId();
        await ApplyDispatchRecoveryAsync(delivery, retry: false, actorId, cancellationToken);
        delivery.MarkProviderAccepted(
            _timeProvider.GetUtcNow().UtcDateTime,
            input.ProviderMessageId,
            actorId: actorId,
            actorType: NotificationDeliveryActorType.Operator,
            eventKind: NotificationDeliveryEventKind.OperatorConfirmed);
        await _deliveryRepository.UpdateAsync(delivery, autoSave: true, cancellationToken);
        return MapReconcileResult(delivery);
    }

    public async Task<ReconcileNotificationDeliveryResultDto> RetryDeliveryAsync(
        RetryNotificationDeliveryInput input,
        CancellationToken cancellationToken = default)
    {
        await EnsurePermissionAsync(NotificationsPermissions.Deliveries.Resend);
        var delivery = await GetReviewDeliveryAsync(input.DeliveryId, cancellationToken);
        var actorId = GetCurrentActorId();
        await ApplyDispatchRecoveryAsync(delivery, retry: true, actorId, cancellationToken);
        delivery.MarkQueuedForOperatorRetry(_timeProvider.GetUtcNow().UtcDateTime, actorId);
        await _deliveryRepository.UpdateAsync(delivery, autoSave: true, cancellationToken);
        return MapReconcileResult(delivery);
    }

    private async Task<ResendNotificationDeliveryResultDto> ScheduleResendAsync(
        Notification notification,
        List<NotificationDelivery> deliveries,
        int failedDeliveryCount,
        CancellationToken cancellationToken)
    {
        if (notification.DispatchStepId.HasValue)
        {
            var actor = GetCurrentActorId();
            foreach (var delivery in deliveries)
            {
                await ApplyDispatchRecoveryAsync(delivery, retry: true, actor, cancellationToken);
                delivery.MarkQueuedForOperatorRetry(_timeProvider.GetUtcNow().UtcDateTime, actor);
                delivery.RecordResendRequested(_timeProvider.GetUtcNow().UtcDateTime, actor, recipientOverride: false);
            }
            await _deliveryRepository.UpdateManyAsync(deliveries, autoSave: true, cancellationToken);
            return new ResendNotificationDeliveryResultDto
            {
                Accepted = true, RequestId = notification.RequestId, NotificationId = notification.Id,
                OriginalNotificationId = notification.Id, QueuedCount = deliveries.Count,
                RecipientCount = deliveries.Count, FailedDeliveryCount = failedDeliveryCount
            };
        }
        await NotificationDispatchRecoveryGate.RejectDetachedCloneAsync(_notificationRepository, notification, cancellationToken);
        var model = DeserializeTemplateModel(notification);
        var scheduleResult = await NotificationContractFactory.ScheduleAsync(
            _deliveryScheduler,
                notification.TenantId,
                new NotificationChannelRequestHost
                {
                    RequestId = Guid.CreateVersion7(),
                    Channel = MapChannel(notification.Channel),
                    Subject = notification.Subject,
                    TemplateName = notification.TemplateName,
                    TemplateModel = model,
                    Recipients = deliveries
                        .Select(delivery => new NotificationRecipientEto
                        {
                            RecipientType = MapRecipientType(delivery.Recipient.RecipientType),
                            RecipientValue = delivery.Recipient.RecipientValue
                        })
                        .ToList(),
                    OccurredAtUtc = _timeProvider.GetUtcNow().UtcDateTime,
                    SourceService = "Notifications.AdminDeliveryResend",
                    RespectUserPreferences = false,
                    PreferenceUserId = null,
                    PreferenceKey = null
                },
            cancellationToken);

        var requestedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var actorId = GetCurrentActorId();
        foreach (var delivery in deliveries)
        {
            delivery.RecordResendRequested(requestedAtUtc, actorId, recipientOverride: false);
        }

        await _deliveryRepository.UpdateManyAsync(deliveries, autoSave: true, cancellationToken);

        return new ResendNotificationDeliveryResultDto
        {
            Accepted = scheduleResult.Accepted,
            IsDuplicate = scheduleResult.IsDuplicate,
            SkippedByPreferences = scheduleResult.SkippedByPreferences,
            RequestId = scheduleResult.RequestId,
            NotificationId = scheduleResult.NotificationId,
            OriginalNotificationId = notification.Id,
            QueuedCount = scheduleResult.QueuedCount,
            RecipientCount = deliveries.Count,
            FailedDeliveryCount = failedDeliveryCount
        };
    }

    private async Task<NotificationDelivery> GetFailedDeliveryAsync(long deliveryId, CancellationToken cancellationToken)
    {
        if (deliveryId <= 0)
        {
            throw new SharedValidationException(
                NotificationErrorCodes.DeliveryIdRequired,
                "DeliveryId is required.");
        }

        var deliveryQueryable = await _deliveryRepository.WithDetailsAsync(x => x.Notification, x => x.Recipient);
        var delivery = await deliveryQueryable
            .FirstOrDefaultAsync(x => x.Id == deliveryId, cancellationToken);

        if (delivery == null)
        {
            throw new EntityNotFoundException(
                NotificationErrorCodes.DeliveryNotFound,
                $"Delivery '{deliveryId}' was not found.");
        }

        if (delivery.Status != NotificationStatus.Failed)
        {
            throw new SharedConflictException(
                NotificationErrorCodes.DeliveryNotFailed,
                "Only failed delivery rows can be resent with an override.");
        }

        return delivery;
    }

    private async Task<NotificationDelivery> GetReviewDeliveryAsync(long deliveryId, CancellationToken cancellationToken)
    {
        if (deliveryId <= 0)
        {
            throw new SharedValidationException(
                NotificationErrorCodes.DeliveryIdRequired,
                "DeliveryId is required.");
        }

        var deliveryQueryable = await _deliveryRepository.WithDetailsAsync(delivery => delivery.Notification);
        var delivery = await deliveryQueryable.Include(delivery => delivery.Notification).FirstOrDefaultAsync(x => x.Id == deliveryId, cancellationToken);

        if (delivery == null)
        {
            throw new EntityNotFoundException(
                NotificationErrorCodes.DeliveryNotFound,
                $"Delivery '{deliveryId}' was not found.");
        }

        if (delivery.Status != NotificationStatus.NeedsReview &&
            delivery.Status != NotificationStatus.DeadLettered)
        {
            throw new SharedConflictException(
                NotificationErrorCodes.DeliveryNotReviewable,
                "Only review-required or dead-lettered delivery rows can be reconciled.");
        }

        return delivery;
    }

    private Task ApplyDispatchRecoveryAsync(NotificationDelivery delivery, bool retry, string actorId, CancellationToken cancellationToken) =>
        NotificationDispatchRecoveryGate.ApplyAsync(_notificationRepository, delivery.Notification, retry,
            _timeProvider.GetUtcNow().UtcDateTime, actorId, cancellationToken);

    private static ReconcileNotificationDeliveryResultDto MapReconcileResult(NotificationDelivery delivery)
    {
        return new ReconcileNotificationDeliveryResultDto
        {
            DeliveryId = delivery.Id,
            Status = delivery.Status,
            DeliveryAttemptId = delivery.DeliveryAttemptId,
            ProviderMessageId = delivery.ProviderMessageId
        };
    }

    private string GetCurrentActorId()
    {
        var principal = _currentPrincipalAccessor.Principal;
        var actorId = principal.FindFirstValue(SharedClaimTypes.Subject)
            ?? principal.FindFirstValue(SharedClaimTypes.UserId);
        if (string.IsNullOrWhiteSpace(actorId))
        {
            throw new UnauthorizedAccessException("Operator identity is required for delivery actions.");
        }

        return actorId.Trim();
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

    private static List<NotificationOverrideRecipientDto> NormalizeOverrideRecipients(IEnumerable<NotificationOverrideRecipientDto>? recipients)
    {
        var normalized = (recipients ?? [])
            .Select(recipient => new NotificationOverrideRecipientDto
            {
                RecipientType = recipient.RecipientType,
                RecipientValue = recipient.RecipientValue?.Trim() ?? string.Empty
            })
            .Where(recipient => !string.IsNullOrWhiteSpace(recipient.RecipientValue))
            .ToList();

        if (normalized.Count == 0)
        {
            throw new SharedValidationException(
                NotificationErrorCodes.OverrideRecipientsRequired,
                "At least one explicit override recipient is required.");
        }

        return normalized;
    }

    private static Dictionary<string, string?> DeserializeTemplateModel(Notification notification)
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
                "The selected notification template model is invalid and cannot be resent.",
                exception);
        }
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
