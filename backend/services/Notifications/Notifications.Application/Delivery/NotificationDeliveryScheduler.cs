using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Permissions.Application.Contracts.Features;
using Notifications.Application.Digests;
using Notifications.Application.Contracts.Delivery;
using Notifications.Application.Contracts.RateLimiting;
using Notifications.Domain.Notifications;
using Notifications.Domain.Shared.Constants;
using Notifications.Domain.Shared.Enums;
using Shared;
using Shared.Permissions.Definitions.Notifications;
using Shared.Domain.Repositories;
using Shared.EventContracts;
using Shared.Modularity.DependencyInjection;
using Shared.Uow.Abstractions;
using Shared.Uow.Options;
using Shared.BackgroundTasks.Workers;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Notifications.Application.Dispatches;
using Notifications.EntityFrameworkCore.EntityFrameworkCore;
using Shared.EntityFrameworkCore.Repositories;

namespace Notifications.Application.Delivery;

public sealed class NotificationDeliveryScheduler : INotificationDeliveryScheduler, ITransientDependency
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Meter DeliveryMeter = new("Notifications.Delivery");
    private static readonly Counter<long> ScheduleAcceptedCounter =
        DeliveryMeter.CreateCounter<long>("notifications.schedule.accepted");
    private static readonly Counter<long> ScheduleDuplicateCounter =
        DeliveryMeter.CreateCounter<long>("notifications.schedule.duplicate");

    private readonly IUnitOfWorkManager _uowManager;
    private readonly IUnitOfWorkExecutor _unitOfWorkExecutor;
    private readonly IRepository<Notification, Guid> _notificationRepository;
    private readonly IRepository<NotificationRecipient, long> _recipientRepository;
    private readonly IRepository<NotificationDelivery, long> _deliveryRepository;
    private readonly NotificationsChannelsOptions _channelsOptions;
    private readonly IFeatureCheckerClient _featureChecker;
    private readonly IIdentityNotificationPreferenceClient _preferenceClient;
    private readonly IBackgroundWorkerWakeSignal _wakeSignal;
    private readonly NotificationsDeliveryOptions _deliveryOptions;
    private readonly INotificationRateLimiter _rateLimiter;
    private readonly INotificationAudienceResolver _audienceResolver;
    private readonly ITenantNotificationSuppressionChecker _suppressionChecker;
    private readonly INotificationDigestPlanner _digestPlanner;
    private readonly ILogger<NotificationDeliveryScheduler> _logger;

    public NotificationDeliveryScheduler(
        IUnitOfWorkManager uowManager,
        IUnitOfWorkExecutor unitOfWorkExecutor,
        IRepository<Notification, Guid> notificationRepository,
        IRepository<NotificationRecipient, long> recipientRepository,
        IRepository<NotificationDelivery, long> deliveryRepository,
        IOptions<NotificationsChannelsOptions> channelsOptions,
        IOptions<NotificationsDeliveryOptions> deliveryOptions,
        IFeatureCheckerClient featureChecker,
        IIdentityNotificationPreferenceClient preferenceClient,
        IBackgroundWorkerWakeSignal wakeSignal,
        INotificationRateLimiter rateLimiter,
        INotificationAudienceResolver audienceResolver,
        ITenantNotificationSuppressionChecker suppressionChecker,
        INotificationDigestPlanner digestPlanner,
        ILogger<NotificationDeliveryScheduler> logger)
    {
        _uowManager = uowManager;
        _unitOfWorkExecutor = unitOfWorkExecutor;
        _notificationRepository = notificationRepository;
        _recipientRepository = recipientRepository;
        _deliveryRepository = deliveryRepository;
        _channelsOptions = channelsOptions.Value;
        _deliveryOptions = deliveryOptions.Value;
        _featureChecker = featureChecker;
        _preferenceClient = preferenceClient;
        _wakeSignal = wakeSignal;
        _rateLimiter = rateLimiter;
        _audienceResolver = audienceResolver;
        _suppressionChecker = suppressionChecker;
        _digestPlanner = digestPlanner;
        _logger = logger;
    }

    public Task<NotificationScheduleResult> ScheduleAsync(
        NotificationChannelRequestTenant input,
        CancellationToken cancellationToken = default)
    {
        EnsureTenantContract(input.TenantId);
        return ScheduleInternalAsync(
            NotificationScheduleCommand.From(input),
            input.TenantId!.Value,
            notBeforeUtc: null,
            cancellationToken);
    }

    public Task<NotificationScheduleResult> ScheduleAsync(
        NotificationChannelRequestHost input,
        CancellationToken cancellationToken = default)
        => ScheduleInternalAsync(
            NotificationScheduleCommand.From(input),
            tenantId: null,
            notBeforeUtc: null,
            cancellationToken);

    public Task<NotificationScheduleResult> ScheduleAtAsync(
        NotificationChannelRequestTenant input,
        DateTime notBeforeUtc,
        CancellationToken cancellationToken = default)
    {
        EnsureTenantContract(input.TenantId);
        return ScheduleInternalAsync(
            NotificationScheduleCommand.From(input),
            input.TenantId!.Value,
            notBeforeUtc,
            cancellationToken);
    }

    public Task<NotificationScheduleResult> ScheduleAtAsync(
        NotificationChannelRequestHost input,
        DateTime notBeforeUtc,
        CancellationToken cancellationToken = default)
        => ScheduleInternalAsync(
            NotificationScheduleCommand.From(input),
            tenantId: null,
            notBeforeUtc,
            cancellationToken);

    private async Task<NotificationScheduleResult> ScheduleInternalAsync(
        NotificationScheduleCommand input,
        Guid? tenantId,
        DateTime? notBeforeUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        ValidateNotBeforeUtc(notBeforeUtc);

        if (tenantId.HasValue &&
            !string.Equals(input.SourceService, "tenant-closure", StringComparison.Ordinal) &&
            await _suppressionChecker.IsSuppressedAsync(tenantId.Value, cancellationToken))
        {
            return new NotificationScheduleResult
            {
                Accepted = true,
                RequestId = input.RequestId == Guid.Empty ? Guid.CreateVersion7() : input.RequestId,
                NotificationId = Guid.Empty,
                QueuedCount = 0
            };
        }

        var requestId = input.RequestId == Guid.Empty
            ? Guid.CreateVersion7()
            : input.RequestId;
        var committed = await FindExistingNotificationAsync(tenantId, requestId, cancellationToken);
        if (committed is not null)
            return new NotificationScheduleResult
            {
                Accepted = true,
                IsDuplicate = true,
                RequestId = requestId,
                NotificationId = committed.Id
            };
        var channel = MapChannel(input.Channel, _channelsOptions);
        var templateName = input.TemplateName?.Trim();
        if (string.IsNullOrWhiteSpace(templateName))
        {
            throw new SharedValidationException(
                NotificationErrorCodes.TemplateRequired,
                "TemplateName is required.");
        }

        await EnsureChannelFeatureEnabledAsync(tenantId, channel, cancellationToken);

        if (input.RespectUserPreferences &&
            input.PreferenceUserId.HasValue &&
            input.PreferenceUserId.Value != Guid.Empty &&
            !string.IsNullOrWhiteSpace(input.PreferenceKey))
        {
            var enabled = await _preferenceClient.IsNotificationEnabledAsync(
                tenantId,
                input.PreferenceUserId.Value,
                input.PreferenceKey,
                cancellationToken);
            if (!enabled)
            {
                return new NotificationScheduleResult
                {
                    Accepted = true,
                    IsDuplicate = false,
                    SkippedByPreferences = true,
                    RequestId = requestId,
                    NotificationId = Guid.Empty,
                    QueuedCount = 0
                };
            }
        }

        var recipientInputs = input.Recipients ?? [];
        if (recipientInputs.Count == 0)
        {
            throw new SharedValidationException(
                NotificationErrorCodes.RecipientsRequired,
                "At least one recipient is required.");
        }

        // Expand group recipient types (Role / AllTenantUsers) into concrete users. Short-circuit on a
        // redelivered request first so a duplicate never triggers audience resolution or rate charge.
        var hasGroupRecipients = recipientInputs.Any(r =>
            r.RecipientType is NotificationSendRecipientType.Role
                or NotificationSendRecipientType.AllTenantUsers);
        if (hasGroupRecipients)
        {
            var existingForGroup = await FindExistingNotificationAsync(tenantId, requestId, cancellationToken);
            if (existingForGroup != null)
            {
                ScheduleAcceptedCounter.Add(1, new KeyValuePair<string, object?>("channel", input.Channel.ToString()));
                ScheduleDuplicateCounter.Add(1, new KeyValuePair<string, object?>("channel", input.Channel.ToString()));
                return new NotificationScheduleResult
                {
                    Accepted = true,
                    IsDuplicate = true,
                    SkippedByPreferences = false,
                    RequestId = requestId,
                    NotificationId = existingForGroup.Id,
                    QueuedCount = 0
                };
            }

            var resolvedRecipients = await _audienceResolver.ResolveAsync(
                tenantId,
                input.Channel,
                recipientInputs,
                cancellationToken);

            if (resolvedRecipients.Count == 0)
            {
                // Empty audience (e.g. role with no users) is a successful no-op, not an error.
                ScheduleAcceptedCounter.Add(1, new KeyValuePair<string, object?>("channel", input.Channel.ToString()));
                return new NotificationScheduleResult
                {
                    Accepted = true,
                    IsDuplicate = false,
                    SkippedByPreferences = false,
                    RequestId = requestId,
                    NotificationId = Guid.Empty,
                    QueuedCount = 0
                };
            }

            recipientInputs = resolvedRecipients.ToList();
        }

        ValidateRecipients(channel, recipientInputs);

        var notification = new Notification(
            id: Guid.CreateVersion7(),
            tenantId: tenantId,
            requestId: requestId,
            channel: channel,
            templateName: templateName,
            templateModelJson: JsonSerializer.Serialize(
                input.TemplateModel ?? new Dictionary<string, string?>(StringComparer.Ordinal),
                JsonOptions),
            subject: input.Subject,
            category: input.Category,
            priority: MapPriority(input.Priority),
            actionUrl: input.ActionUrl,
            scheduledForUtc: notBeforeUtc,
            preferenceUserId: input.PreferenceUserId?.ToString("D"),
            sourceService: input.SourceService);

        var recipients = recipientInputs
            .Where(x => !string.IsNullOrWhiteSpace(x.RecipientValue))
            .Select(x => new NotificationRecipient(
                id: 0,
                tenantId: tenantId,
                notificationId: notification.Id,
                recipientType: MapRecipientType(x.RecipientType),
                recipientValue: x.RecipientValue.Trim()))
            .GroupBy(x => $"{(int)x.RecipientType}:{x.RecipientValue}", StringComparer.Ordinal)
            .Select(x => x.First())
            .ToList();

        if (recipients.Count == 0)
        {
            throw new SharedValidationException(
                NotificationErrorCodes.ValidRecipientRequired,
                "At least one valid recipient is required.");
        }

        // Per-tenant / per-channel quota enforced before any write. On exceed this throws a typed
        // exception (recorded as a metric) rather than silently dropping the request.
        if (!input.DigestBucketId.HasValue)
            await _rateLimiter.EnsureWithinLimitAsync(requestId, tenantId, channel, recipients.Count, cancellationToken);

        try
        {
            // Joins the caller's unit of work when one is active (a request or the messaging inbox transaction), so
            // the schedule commits with it; otherwise it owns one. The executor is RequiresNew and would split request
            // chronology.
            using var uow = _uowManager.Begin(new UnitOfWorkOptions { IsTransactional = true });

            var notificationQueryable = await _notificationRepository.GetQueryableAsync();
            var existing = await notificationQueryable
                .IgnoreQueryFilters()
                .Where(x => x.TenantId == tenantId && x.RequestId == requestId)
                .AsNoTracking()
                .FirstOrDefaultAsync(cancellationToken);
            if (existing != null)
            {
                ScheduleAcceptedCounter.Add(1, new KeyValuePair<string, object?>("channel", input.Channel.ToString()));
                ScheduleDuplicateCounter.Add(1, new KeyValuePair<string, object?>("channel", input.Channel.ToString()));
                await uow.CompleteAsync(cancellationToken);
                return new NotificationScheduleResult
                {
                    Accepted = true,
                    IsDuplicate = true,
                    SkippedByPreferences = false,
                    RequestId = requestId,
                    NotificationId = existing.Id,
                    QueuedCount = 0
                };
            }

            if (input.DispatchStepId.HasValue)
            {
                if (!input.SchedulingClaimId.HasValue || _notificationRepository is not IEfCoreRepository<Notification> repository ||
                    await repository.GetDbContextAsync() is not NotificationsDbContextApp context)
                    throw NotificationDispatchGate.Conflict();
                await NotificationDispatchGate.LinkAsync(context, notification, input.DispatchStepId.Value,
                    input.SchedulingClaimId.Value, DateTime.UtcNow, cancellationToken);
            }
            if (input.DigestBucketId.HasValue)
            {
                if (_notificationRepository is not IEfCoreRepository<Notification> digestRepository ||
                    await digestRepository.GetDbContextAsync() is not NotificationsDbContextApp digestContext)
                    throw NotificationDispatchGate.Conflict();
                if (!await NotificationDispatchGate.LinkDigestAsync(digestContext, notification, input.DigestBucketId.Value,
                        recipients, DateTime.UtcNow, cancellationToken))
                {
                    await uow.CompleteAsync(cancellationToken);
                    return new NotificationScheduleResult { Accepted = true, RequiresReview = true, RequestId = requestId };
                }
            }
            await _notificationRepository.InsertAsync(notification, autoSave: false, cancellationToken);
            await _recipientRepository.InsertManyAsync(recipients, autoSave: true, cancellationToken);

            var deliveries = new List<NotificationDelivery>();
            foreach (var recipient in recipients)
            {
                if (await _digestPlanner.TryAddAsync(notification, recipient, cancellationToken))
                {
                    continue;
                }

                deliveries.Add(new NotificationDelivery(
                    id: 0,
                    tenantId: tenantId,
                    notificationId: notification.Id,
                    recipientId: recipient.Id,
                    notBeforeUtc: notBeforeUtc));
            }

            await _deliveryRepository.InsertManyAsync(deliveries, autoSave: false, cancellationToken);

            if (deliveries.Count > 0)
            {
                uow.OnCompleted(SignalDeliveryWorkerAfterCommitAsync);
            }

            await uow.CompleteAsync(cancellationToken);

            ScheduleAcceptedCounter.Add(1, new KeyValuePair<string, object?>("channel", input.Channel.ToString()));

            return new NotificationScheduleResult
            {
                Accepted = true,
                IsDuplicate = false,
                SkippedByPreferences = false,
                RequestId = requestId,
                NotificationId = notification.Id,
                QueuedCount = deliveries.Count,
                ScheduledForUtc = notBeforeUtc
            };
        }
        catch (SharedConflictException ex) when (
            string.Equals(ex.Code, "notification_request_duplicate", StringComparison.Ordinal))
        {
            // A conflict inside an existing request/consumer owner invalidates that owner's
            // transaction. Let it roll back so redelivery can observe the committed winner.
            if (_uowManager.Current != null)
            {
                throw;
            }

            var duplicateCheck = await FindExistingNotificationInIndependentScopeAsync(
                tenantId,
                requestId,
                cancellationToken);
            if (duplicateCheck != null)
            {
                ScheduleAcceptedCounter.Add(1, new KeyValuePair<string, object?>("channel", input.Channel.ToString()));
                ScheduleDuplicateCounter.Add(1, new KeyValuePair<string, object?>("channel", input.Channel.ToString()));
                return new NotificationScheduleResult
                {
                    Accepted = true,
                    IsDuplicate = true,
                    SkippedByPreferences = false,
                    RequestId = requestId,
                    NotificationId = duplicateCheck.Id,
                    QueuedCount = 0
                };
            }

            throw;
        }
    }

    private Task<Notification?> FindExistingNotificationAsync(
        Guid? tenantId,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        return _uowManager.Current != null
            ? QueryExistingNotificationAsync(tenantId, requestId, cancellationToken)
            : FindExistingNotificationInIndependentScopeAsync(tenantId, requestId, cancellationToken);
    }

    private Task<Notification?> FindExistingNotificationInIndependentScopeAsync(
        Guid? tenantId,
        Guid requestId,
        CancellationToken cancellationToken)
        => _unitOfWorkExecutor.ExecuteNonTransactionalAsync(
            operationToken => QueryExistingNotificationAsync(tenantId, requestId, operationToken),
            cancellationToken);

    private async Task<Notification?> QueryExistingNotificationAsync(
        Guid? tenantId,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        var queryable = await _notificationRepository.GetQueryableAsync();
        return await queryable
            .IgnoreQueryFilters()
            .Where(x => x.TenantId == tenantId && x.RequestId == requestId)
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task SignalDeliveryWorkerAfterCommitAsync()
    {
        try
        {
            await _wakeSignal.SignalAsync(
                NotificationDeliveryWakeKeys.Delivery,
                NotificationDeliveryWakeKeys.CreateWakeOptions(_deliveryOptions),
                cancellationToken: CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The periodic safety poll is the durable fallback; wake failure must not turn an
            // already committed schedule into a caller-visible transaction failure.
            _logger.LogWarning(ex, "Failed to signal the notification delivery worker after commit.");
        }
    }

    private static NotificationPriority MapPriority(NotificationSendPriority priority)
    {
        return priority switch
        {
            NotificationSendPriority.Low => NotificationPriority.Low,
            NotificationSendPriority.Normal => NotificationPriority.Normal,
            NotificationSendPriority.High => NotificationPriority.High,
            NotificationSendPriority.Critical => NotificationPriority.Critical,
            _ => NotificationPriority.Normal
        };
    }

    private static void EnsureTenantContract(Guid? tenantId)
    {
        if (!tenantId.HasValue || tenantId.Value == Guid.Empty)
        {
            throw new SharedValidationException(
                NotificationErrorCodes.TenantRequired,
                "Tenant notification commands require a non-empty tenant id.");
        }
    }

    private void ValidateNotBeforeUtc(DateTime? notBeforeUtc)
    {
        if (!notBeforeUtc.HasValue)
        {
            return;
        }

        if (notBeforeUtc.Value.Kind != DateTimeKind.Utc)
        {
            throw new SharedValidationException(
                NotificationErrorCodes.ScheduleUtcRequired,
                "Scheduled delivery time must be supplied in UTC.");
        }

        var now = DateTime.UtcNow;
        if (notBeforeUtc.Value <= now)
        {
            throw new SharedValidationException(
                NotificationErrorCodes.ScheduleMustBeFuture,
                "Scheduled delivery time must be in the future.");
        }

        var maximum = now.AddDays(Math.Clamp(_deliveryOptions.MaxScheduleHorizonDays, 1, 365));
        if (notBeforeUtc.Value > maximum)
        {
            throw new SharedValidationException(
                NotificationErrorCodes.ScheduleHorizonExceeded,
                "Scheduled delivery time exceeds the configured scheduling horizon.");
        }
    }

    private static NotificationChannel MapChannel(NotificationSendChannel channel, NotificationsChannelsOptions options)
    {
        return channel switch
        {
            NotificationSendChannel.Email when options.EmailEnabled => NotificationChannel.Email,
            NotificationSendChannel.App when options.AppEnabled => NotificationChannel.App,
            NotificationSendChannel.Sms when options.SmsEnabled => NotificationChannel.Sms,
            NotificationSendChannel.Push when options.PushEnabled => NotificationChannel.Push,
            NotificationSendChannel.WhatsApp when options.WhatsAppEnabled => NotificationChannel.WhatsApp,
            NotificationSendChannel.Email => throw new NotSupportedException("Email channel is disabled in this deployment."),
            NotificationSendChannel.App => throw new NotSupportedException("App channel is disabled in this deployment."),
            NotificationSendChannel.Sms => throw new NotSupportedException("Sms channel is disabled in this deployment."),
            NotificationSendChannel.Push => throw new NotSupportedException("Push channel is disabled in this deployment."),
            NotificationSendChannel.WhatsApp => throw new NotSupportedException("WhatsApp channel is disabled in this deployment."),
            _ => throw new NotSupportedException($"Unsupported notification channel: {channel}")
        };
    }

    private async Task EnsureChannelFeatureEnabledAsync(
        Guid? tenantId,
        NotificationChannel channel,
        CancellationToken cancellationToken)
    {
        var featureName = channel switch
        {
            NotificationChannel.Email => NotificationsFeatures.Email,
            NotificationChannel.Sms => NotificationsFeatures.Sms,
            NotificationChannel.Push => NotificationsFeatures.Push,
            NotificationChannel.WhatsApp => NotificationsFeatures.WhatsApp,
            _ => null
        };

        if (string.IsNullOrWhiteSpace(featureName))
        {
            return;
        }

        var isEnabled = await _featureChecker.IsEnabledAsync(tenantId, featureName, cancellationToken);
        if (!isEnabled)
        {
            throw new NotificationChannelDisabledByFeatureException(
                channel.ToString(),
                featureName);
        }
    }

    private static NotificationRecipientType MapRecipientType(NotificationSendRecipientType recipientType)
    {
        return recipientType switch
        {
            NotificationSendRecipientType.EmailAddress => NotificationRecipientType.EmailAddress,
            NotificationSendRecipientType.PhoneNumber => NotificationRecipientType.PhoneNumber,
            NotificationSendRecipientType.UserId => NotificationRecipientType.UserId,
            NotificationSendRecipientType.DeviceToken => NotificationRecipientType.DeviceToken,
            _ => throw new NotSupportedException($"Unsupported notification recipient type: {recipientType}")
        };
    }

    private static void ValidateRecipients(
        NotificationChannel channel,
        IReadOnlyCollection<NotificationRecipientEto> recipients)
    {
        foreach (var recipient in recipients)
        {
            var isCompatible = channel switch
            {
                NotificationChannel.Email => recipient.RecipientType == NotificationSendRecipientType.EmailAddress,
                NotificationChannel.App => recipient.RecipientType == NotificationSendRecipientType.UserId,
                NotificationChannel.Sms => recipient.RecipientType == NotificationSendRecipientType.PhoneNumber,
                NotificationChannel.Push => recipient.RecipientType == NotificationSendRecipientType.DeviceToken,
                NotificationChannel.WhatsApp => recipient.RecipientType == NotificationSendRecipientType.PhoneNumber,
                _ => false
            };

            if (!isCompatible)
            {
                throw new SharedValidationException(
                    NotificationErrorCodes.RecipientChannelMismatch,
                    $"Recipient type '{recipient.RecipientType}' is not valid for channel '{channel}'.");
            }
        }
    }

    private sealed class NotificationScheduleCommand
    {
        public Guid RequestId { get; init; }
        public Guid? DispatchStepId { get; init; }
        public Guid? SchedulingClaimId { get; init; }
        public Guid? DigestBucketId { get; init; }
        public NotificationSendChannel Channel { get; init; }
        public string? Subject { get; init; }
        public string TemplateName { get; init; } = string.Empty;
        public Dictionary<string, string?> TemplateModel { get; init; } = new(StringComparer.Ordinal);
        public List<NotificationRecipientEto> Recipients { get; init; } = [];
        public string? Category { get; init; }
        public NotificationSendPriority Priority { get; init; }
        public string? ActionUrl { get; init; }
        public string SourceService { get; init; } = string.Empty;
        public bool RespectUserPreferences { get; init; }
        public Guid? PreferenceUserId { get; init; }
        public string? PreferenceKey { get; init; }

        public static NotificationScheduleCommand From(NotificationChannelRequestTenant input)
        {
            ArgumentNullException.ThrowIfNull(input);
            return new NotificationScheduleCommand
            {
                RequestId = input.RequestId,
                DispatchStepId = input.DispatchStepId,
                SchedulingClaimId = input.SchedulingClaimId,
                DigestBucketId = input.DigestBucketId,
                Channel = input.Channel,
                Subject = input.Subject,
                TemplateName = input.TemplateName,
                TemplateModel = input.TemplateModel,
                Recipients = input.Recipients,
                Category = input.Category,
                Priority = input.Priority,
                ActionUrl = input.ActionUrl,
                SourceService = input.SourceService,
                RespectUserPreferences = input.RespectUserPreferences,
                PreferenceUserId = input.PreferenceUserId,
                PreferenceKey = input.PreferenceKey
            };
        }

        public static NotificationScheduleCommand From(NotificationChannelRequestHost input)
        {
            ArgumentNullException.ThrowIfNull(input);
            return new NotificationScheduleCommand
            {
                RequestId = input.RequestId,
                DispatchStepId = input.DispatchStepId,
                SchedulingClaimId = input.SchedulingClaimId,
                DigestBucketId = input.DigestBucketId,
                Channel = input.Channel,
                Subject = input.Subject,
                TemplateName = input.TemplateName,
                TemplateModel = input.TemplateModel,
                Recipients = input.Recipients,
                Category = input.Category,
                Priority = input.Priority,
                ActionUrl = input.ActionUrl,
                SourceService = input.SourceService,
                RespectUserPreferences = input.RespectUserPreferences,
                PreferenceUserId = input.PreferenceUserId,
                PreferenceKey = input.PreferenceKey
            };
        }
    }
}
