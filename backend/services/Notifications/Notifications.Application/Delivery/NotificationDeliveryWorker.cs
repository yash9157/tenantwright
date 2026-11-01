using Notifications.EntityFrameworkCore.EntityFrameworkCore;
using System.Text.Json;
using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Notifications.Application.Policy;
using Notifications.Application.Contracts.Delivery;
using Notifications.Application.Contracts.Realtime;
using Notifications.Application.Contracts.Templates;
using Notifications.Domain.Notifications;
using Notifications.Domain.Shared.Constants;
using Notifications.Domain.Shared.Enums;
using Shared.AuditLogs.Publishing;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;
using Shared.Uow.Abstractions;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Shared.EntityFrameworkCore;
using Shared.EntityFrameworkCore.TenantDatabases;

namespace Notifications.Application.Delivery;

public sealed class NotificationDeliveryWorker : INotificationDeliveryWorker, ITransientDependency
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Meter DeliveryMeter = new("Notifications.Delivery");
    private static readonly Counter<long> DeliverySuccessCounter =
        DeliveryMeter.CreateCounter<long>("notifications.delivery.success");
    private static readonly Counter<long> DeliveryFailedCounter =
        DeliveryMeter.CreateCounter<long>("notifications.delivery.failed");
    private static readonly Counter<long> DeliveryRetryCounter =
        DeliveryMeter.CreateCounter<long>("notifications.delivery.retry");
    private static readonly Counter<long> DeliveryNeedsReviewCounter =
        DeliveryMeter.CreateCounter<long>("notifications.delivery.needs_review");
    private static readonly Counter<long> DeliveryDeadLetteredCounter =
        DeliveryMeter.CreateCounter<long>("notifications.delivery.dead_lettered");
    private static readonly Counter<long> DeliveryQuietHoursHeldCounter =
        DeliveryMeter.CreateCounter<long>("notifications.delivery.quiet_hours.held");
    private static readonly Counter<long> DeliveryQuietHoursReleasedCounter =
        DeliveryMeter.CreateCounter<long>("notifications.delivery.quiet_hours.released");
    private static readonly Counter<long> DeliveryQuietHoursBypassedCounter =
        DeliveryMeter.CreateCounter<long>("notifications.delivery.quiet_hours.bypassed");
    private static readonly Histogram<double> DeliveryProcessDurationMs =
        DeliveryMeter.CreateHistogram<double>("notifications.delivery.process.duration.ms");
    private static readonly Histogram<double> DeliveryQueueLagMs =
        DeliveryMeter.CreateHistogram<double>("notifications.delivery.queue.lag.ms");

    private readonly IUnitOfWorkExecutor _unitOfWorkExecutor;
    private readonly IDbContextProvider<NotificationsDbContextApp> _dataContextProvider;
    private readonly Dictionary<NotificationChannel, IChannelDeliveryHandler> _handlers;
    private readonly INotificationsRealtimePublisher _realtimePublisher;
    private readonly IAuditLogWriteRequestPublisher _auditLogPublisher;
    private readonly ICurrentTenant _currentTenant;
    private readonly INotificationsTemplateRenderer _templateRenderer;
    private readonly NotificationsDeliveryOptions _options;
    private readonly NotificationDeliveryPolicyResolver _policyResolver;
    private readonly ILogger<NotificationDeliveryWorker> _logger;

    public NotificationDeliveryWorker(
        IUnitOfWorkExecutor unitOfWorkExecutor,
        IDbContextProvider<NotificationsDbContextApp> dataContextProvider,
        IEnumerable<IChannelDeliveryHandler> handlers,
        INotificationsRealtimePublisher realtimePublisher,
        IAuditLogWriteRequestPublisher auditLogPublisher,
        ICurrentTenant currentTenant,
        INotificationsTemplateRenderer templateRenderer,
        IOptions<NotificationsDeliveryOptions> options,
        ILogger<NotificationDeliveryWorker> logger)
    {
        _unitOfWorkExecutor = unitOfWorkExecutor;
        _dataContextProvider = dataContextProvider;
        _handlers = handlers
            .GroupBy(x => x.Channel)
            .ToDictionary(x => x.Key, x => x.First());
        _realtimePublisher = realtimePublisher;
        _auditLogPublisher = auditLogPublisher;
        _currentTenant = currentTenant;
        _templateRenderer = templateRenderer;
        _options = options.Value;
        _policyResolver = new NotificationDeliveryPolicyResolver(dataContextProvider);
        _logger = logger;
    }

    public async Task<int> ProcessBatchAsync(CancellationToken cancellationToken = default)
    {
        var batchSize = Math.Clamp(_options.BatchSize, 1, 500);
        var maxRetryCount = Math.Max(1, _options.MaxRetryCount);
        var dueNow = DateTime.UtcNow;

        var dueDeliveryIds = await _unitOfWorkExecutor.ExecuteNonTransactionalAsync(
            async operationToken =>
            {
                var unitOfWorkDbContext = await _dataContextProvider.GetDbContextAsync();
                // Under database fan-out only rows this database owns are claimed: delivery switches to the row's
                // tenant scope, and a tenant with a dedicated database must never be served from rows it left behind
                // in the shared database.
                return await unitOfWorkDbContext.NotificationDeliveries
                    .IgnoreQueryFilters()
                    .WhereOwnedByCurrentDatabase()
                    .Where(x =>
                        ((x.Status == NotificationStatus.Queued ||
                          x.Status == NotificationStatus.Failed) &&
                         x.TryCount < maxRetryCount ||
                         x.Status == NotificationStatus.Processing) &&
                        (!x.NextAttemptTimeUtc.HasValue || x.NextAttemptTimeUtc <= dueNow))
                    .OrderBy(x => x.NextAttemptTimeUtc ?? x.CreationTime)
                    .ThenBy(x => x.CreationTime)
                    .Select(x => x.Id)
                    .Take(batchSize)
                    .ToListAsync(operationToken);
            },
            cancellationToken);

        if (dueDeliveryIds.Count == 0)
        {
            return 0;
        }

        var templateModelCache = new Dictionary<Guid, Dictionary<string, string?>>();
        var processedCount = 0;

        foreach (var deliveryId in dueDeliveryIds)
        {
            try
            {
                var claimed = await TryClaimDeliveryAsync(
                    deliveryId,
                    maxRetryCount,
                    templateModelCache,
                    cancellationToken);
                if (claimed == null)
                {
                    continue;
                }

                processedCount++;
                DeliveryQueueLagMs.Record(
                    claimed.QueueLagMs,
                    new KeyValuePair<string, object?>("channel", claimed.Notification.Channel.ToString()));

                var processStarted = Stopwatch.GetTimestamp();
                var result = await DeliverAsync(claimed, cancellationToken);
                var renderedInboxContent = await RenderInboxContentIfRequiredAsync(
                    claimed,
                    result,
                    cancellationToken);
                var finalized = await FinalizeDeliveryAsync(
                    claimed,
                    result,
                    renderedInboxContent,
                    maxRetryCount,
                    cancellationToken);
                var processDurationMs = Stopwatch.GetElapsedTime(processStarted).TotalMilliseconds;

                if (finalized == null)
                {
                    _logger.LogWarning(
                        "Skipped post-processing for stale notification delivery attempt {DeliveryAttemptId} on delivery {DeliveryId}.",
                        claimed.DeliveryAttemptId,
                        claimed.DeliveryId);
                    continue;
                }

                DeliveryProcessDurationMs.Record(
                    processDurationMs,
                    new KeyValuePair<string, object?>("channel", claimed.Notification.Channel.ToString()));

                if (result.IsSuccess)
                {
                    DeliverySuccessCounter.Add(
                        1,
                        new KeyValuePair<string, object?>("channel", claimed.Notification.Channel.ToString()));
                }
                else
                {
                    DeliveryFailedCounter.Add(
                        1,
                        new KeyValuePair<string, object?>("channel", claimed.Notification.Channel.ToString()));

                    if (finalized.WillRetry)
                    {
                        DeliveryRetryCounter.Add(
                            1,
                            new KeyValuePair<string, object?>("channel", claimed.Notification.Channel.ToString()));
                    }

                    if (finalized.IsDeadLettered)
                    {
                        DeliveryDeadLetteredCounter.Add(
                            1,
                            new KeyValuePair<string, object?>("channel", claimed.Notification.Channel.ToString()));
                    }
                }

                await TryPublishDeliveryAuditAsync(claimed, result, finalized, processDurationMs, cancellationToken);

                if (finalized.RealtimeSignal != null)
                {
                    await PublishRealtimeSignalAsync(finalized.RealtimeSignal, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed while processing notification delivery {DeliveryId}.",
                    deliveryId);
            }
        }

        return processedCount;
    }

    private async Task<ClaimedDelivery?> TryClaimDeliveryAsync(
        long deliveryId,
        int maxRetryCount,
        Dictionary<Guid, Dictionary<string, string?>> templateModelCache,
        CancellationToken cancellationToken)
    {
        var claimStartedAt = DateTime.UtcNow;
        var claimExpiresAt = claimStartedAt.AddSeconds(Math.Clamp(_options.ProcessingLeaseSeconds, 5, 600));

        var claimResult = await _unitOfWorkExecutor.ExecuteTransactionalAsync(
            async operationToken =>
            {
                var unitOfWorkDbContext = await _dataContextProvider.GetDbContextAsync();
                var delivery = await unitOfWorkDbContext.NotificationDeliveries
                    .IgnoreQueryFilters()
                    .Include(x => x.Notification)
                    .Include(x => x.Recipient)
                    .FirstOrDefaultAsync(x => x.Id == deliveryId, operationToken);
                if (delivery == null || !IsDeliveryDue(delivery, claimStartedAt, maxRetryCount))
                {
                    return DeliveryClaimResult.None;
                }

                if (delivery.Status == NotificationStatus.Processing)
                {
                    var expiredAttemptId = delivery.DeliveryAttemptId;
                    delivery.MarkNeedsReview("provider_outcome_ambiguous", claimStartedAt);
                    return new DeliveryClaimResult
                    {
                        Disposition = DeliveryClaimDisposition.NeedsReview,
                        Channel = delivery.Notification.Channel,
                        DeliveryId = delivery.Id,
                        DeliveryAttemptId = expiredAttemptId
                    };
                }

                var policyDecision = await _policyResolver.EvaluateAsync(
                    delivery.Notification,
                    delivery.Recipient,
                    claimStartedAt,
                    operationToken);
                if (policyDecision.ShouldHold)
                {
                    delivery.HoldForQuietHours(policyDecision.NextAllowedAtUtc!.Value, claimStartedAt);
                    return new DeliveryClaimResult
                    {
                        Disposition = DeliveryClaimDisposition.QuietHoursHeld,
                        Channel = delivery.Notification.Channel,
                        DeliveryId = delivery.Id
                    };
                }

                var policyHoldReleased = delivery.PolicyHeldUntilUtc.HasValue;
                if (policyHoldReleased)
                {
                    delivery.ReleaseQuietHoursHold(claimStartedAt);
                }

                if (!templateModelCache.TryGetValue(delivery.NotificationId, out var templateModel))
                {
                    if (!TryDeserializeTemplateModel(delivery.Notification.TemplateModelJson, out templateModel))
                    {
                        delivery.MarkDeadLettered("template_model_invalid", claimStartedAt);
                        return new DeliveryClaimResult
                        {
                            Disposition = DeliveryClaimDisposition.DeadLettered,
                            Channel = delivery.Notification.Channel,
                            DeliveryId = delivery.Id,
                            PolicyHoldReleased = policyHoldReleased,
                            PolicyBypassReason = policyDecision.Bypassed ? policyDecision.Reason : null
                        };
                    }

                    templateModelCache[delivery.NotificationId] = templateModel;
                }

                var dueReferenceUtc = delivery.NextAttemptTimeUtc ?? delivery.CreationTime;
                var deliveryAttemptId = Guid.CreateVersion7();
                delivery.MarkProcessing(claimStartedAt, claimExpiresAt, deliveryAttemptId);

                return new DeliveryClaimResult
                {
                    Disposition = DeliveryClaimDisposition.Claimed,
                    Channel = delivery.Notification.Channel,
                    DeliveryId = delivery.Id,
                    DeliveryAttemptId = deliveryAttemptId,
                    PolicyHoldReleased = policyHoldReleased,
                    PolicyBypassReason = policyDecision.Bypassed ? policyDecision.Reason : null,
                    ClaimedDelivery = new ClaimedDelivery
                    {
                        DeliveryId = delivery.Id,
                        DeliveryAttemptId = deliveryAttemptId,
                        NotificationId = delivery.NotificationId,
                        TenantId = delivery.TenantId,
                        Notification = delivery.Notification,
                        Recipient = delivery.Recipient,
                        TemplateModel = templateModel,
                        QueueLagMs = Math.Max(
                            0,
                            (claimStartedAt - dueReferenceUtc).TotalMilliseconds)
                    }
                };
            },
            cancellationToken);

        ObserveClaimResult(claimResult);
        return claimResult.ClaimedDelivery;
    }

    private void ObserveClaimResult(DeliveryClaimResult claimResult)
    {
        if (claimResult.PolicyHoldReleased && claimResult.Channel.HasValue)
        {
            DeliveryQuietHoursReleasedCounter.Add(
                1,
                new KeyValuePair<string, object?>("channel", claimResult.Channel.Value.ToString()));
        }

        if (!string.IsNullOrWhiteSpace(claimResult.PolicyBypassReason))
        {
            DeliveryQuietHoursBypassedCounter.Add(
                1,
                new KeyValuePair<string, object?>("reason", claimResult.PolicyBypassReason));
        }

        switch (claimResult.Disposition)
        {
            case DeliveryClaimDisposition.NeedsReview:
                DeliveryNeedsReviewCounter.Add(
                    1,
                    new KeyValuePair<string, object?>("channel", claimResult.Channel!.Value.ToString()));
                _logger.LogWarning(
                    "Notification delivery {DeliveryId} requires operator review because attempt {DeliveryAttemptId} exceeded its processing lease.",
                    claimResult.DeliveryId,
                    claimResult.DeliveryAttemptId);
                break;
            case DeliveryClaimDisposition.QuietHoursHeld:
                DeliveryQuietHoursHeldCounter.Add(
                    1,
                    new KeyValuePair<string, object?>("channel", claimResult.Channel!.Value.ToString()));
                break;
            case DeliveryClaimDisposition.DeadLettered:
                DeliveryDeadLetteredCounter.Add(
                    1,
                    new KeyValuePair<string, object?>("channel", claimResult.Channel!.Value.ToString()));
                _logger.LogWarning(
                    "Notification delivery {DeliveryId} was dead-lettered because its template model is invalid.",
                    claimResult.DeliveryId);
                break;
        }
    }

    private async Task<ChannelDeliveryResult> DeliverAsync(
        ClaimedDelivery claimed,
        CancellationToken cancellationToken)
    {
        if (!_handlers.TryGetValue(claimed.Notification.Channel, out var handler))
        {
            return ChannelDeliveryResult.Failed("channel_not_supported");
        }

        try
        {
            using (_currentTenant.Change(claimed.TenantId))
            {
                return await handler.DeliverAsync(
                    new ChannelDeliveryContext
                    {
                        TenantId = claimed.TenantId,
                        NotificationId = claimed.NotificationId,
                        DeliveryAttemptId = claimed.DeliveryAttemptId,
                        TemplateName = claimed.Notification.TemplateName,
                        Subject = claimed.Notification.Subject,
                        RecipientType = claimed.Recipient.RecipientType,
                        RecipientValue = claimed.Recipient.RecipientValue,
                        TemplateModel = claimed.TemplateModel
                    },
                    cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (NotificationChannelUnavailableException ex)
        {
            _logger.LogWarning(
                "Notification channel unavailable. DeliveryId={DeliveryId}, NotificationId={NotificationId}, Channel={Channel}, Reason={Reason}",
                claimed.DeliveryId,
                claimed.NotificationId,
                claimed.Notification.Channel,
                ex.ErrorCode);
            return ChannelDeliveryResult.Unavailable(ex.ErrorCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Notification delivery handler failed. DeliveryId={DeliveryId}, NotificationId={NotificationId}, Channel={Channel}, TemplateName={TemplateName}",
                claimed.DeliveryId,
                claimed.NotificationId,
                claimed.Notification.Channel,
                claimed.Notification.TemplateName);
            return ChannelDeliveryResult.Failed(SanitizeException(ex));
        }
    }

    private async Task<RenderedInboxContent?> RenderInboxContentIfRequiredAsync(
        ClaimedDelivery claimed,
        ChannelDeliveryResult result,
        CancellationToken cancellationToken)
    {
        if (!result.IsSuccess ||
            claimed.Notification.Channel != NotificationChannel.App ||
            string.IsNullOrWhiteSpace(result.UserId))
        {
            return null;
        }

        try
        {
            return await _unitOfWorkExecutor.ExecuteNonTransactionalAsync(
                async operationToken =>
                {
                    using (_currentTenant.Change(claimed.TenantId))
                    {
                        var model = claimed.TemplateModel is null
                            ? new Dictionary<string, string?>(StringComparer.Ordinal)
                            : new Dictionary<string, string?>(claimed.TemplateModel, StringComparer.Ordinal);

                        // Render content-only (no email layout) so the inbox body carries no email chrome.
                        var rendered = await _templateRenderer.RenderAsync(
                            claimed.Notification.TemplateName,
                            model,
                            applyLayout: false,
                            operationToken);

                        var body = NotificationTextFormatter.ToStructuredText(rendered.Content);
                        var renderedSubject = string.IsNullOrWhiteSpace(rendered.Subject)
                            ? null
                            : rendered.Subject.Trim();
                        var title = renderedSubject
                            ?? (string.IsNullOrWhiteSpace(claimed.Notification.Subject)
                                ? null
                                : claimed.Notification.Subject.Trim())
                            ?? FirstLine(body)
                            ?? claimed.Notification.TemplateName;

                        return new RenderedInboxContent(
                            title,
                            FirstLine(body),
                            string.IsNullOrWhiteSpace(body) ? null : body);
                    }
                },
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Inbox rendering is best-effort: never fail delivery finalization because of it.
            _logger.LogWarning(
                ex,
                "Failed to render inbox content. NotificationId={NotificationId}, TemplateName={TemplateName}",
                claimed.NotificationId,
                claimed.Notification.TemplateName);
            return RenderedInboxContent.Empty;
        }
    }

    private static string? FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var newlineIndex = text.IndexOf('\n', StringComparison.Ordinal);
        var firstLine = (newlineIndex >= 0 ? text[..newlineIndex] : text).Trim();
        return string.IsNullOrWhiteSpace(firstLine) ? null : firstLine;
    }

    private Task<FinalizedDelivery?> FinalizeDeliveryAsync(
        ClaimedDelivery claimed,
        ChannelDeliveryResult result,
        RenderedInboxContent? renderedInboxContent,
        int maxRetryCount,
        CancellationToken cancellationToken)
        => _unitOfWorkExecutor.ExecuteTransactionalAsync(
            async operationToken =>
            {
                var unitOfWorkDbContext = await _dataContextProvider.GetDbContextAsync();
                var delivery = await unitOfWorkDbContext.NotificationDeliveries
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(x => x.Id == claimed.DeliveryId, operationToken);
                if (delivery == null ||
                    delivery.Status != NotificationStatus.Processing ||
                    delivery.DeliveryAttemptId != claimed.DeliveryAttemptId)
                {
                    return null;
                }

                AppNotificationSignalDto? realtimeSignal = null;
                if (result.IsSuccess)
                {
                    if (claimed.Notification.Channel == NotificationChannel.App)
                    {
                        delivery.MarkDelivered(DateTime.UtcNow, result.ProviderMessageId, result.ProviderConfigId);
                    }
                    else
                    {
                        delivery.MarkProviderAccepted(DateTime.UtcNow, result.ProviderMessageId, result.ProviderConfigId);
                    }

                    if (claimed.Notification.Channel == NotificationChannel.App &&
                        !string.IsNullOrWhiteSpace(result.UserId))
                    {
                        var userId = result.UserId.Trim();
                        var exists = await unitOfWorkDbContext.UserNotifications
                            .IgnoreQueryFilters()
                            .AsNoTracking()
                            .AnyAsync(
                                x => x.TenantId == claimed.TenantId &&
                                     x.NotificationId == claimed.NotificationId &&
                                     x.UserId == userId,
                                operationToken);

                        if (!exists)
                        {
                            var userNotification = new UserNotification(
                                id: 0,
                                tenantId: claimed.TenantId,
                                notificationId: claimed.NotificationId,
                                userId: userId);
                            userNotification.SetRenderedContent(
                                renderedInboxContent?.Title,
                                renderedInboxContent?.Summary,
                                renderedInboxContent?.Body);

                            await unitOfWorkDbContext.AddUserNotificationAsync(userNotification, operationToken);
                        }

                        realtimeSignal = new AppNotificationSignalDto
                        {
                            NotificationId = claimed.NotificationId,
                            TenantId = claimed.TenantId,
                            UserId = userId,
                            TemplateName = claimed.Notification.TemplateName,
                            Subject = claimed.Notification.Subject,
                            CreationTimeUtc = claimed.Notification.CreationTime
                        };
                    }
                }
                else
                {
                    if (result.IsUnavailable)
                    {
                        delivery.MarkNeedsReview(NormalizeError(result.ErrorCode), DateTime.UtcNow);
                        return new FinalizedDelivery
                        {
                            DeliveryId = claimed.DeliveryId,
                            RealtimeSignal = null,
                            WillRetry = false,
                            IsDeadLettered = false
                        };
                    }

                    var willRetry = delivery.TryCount < maxRetryCount;
                    if (willRetry)
                    {
                        var retryDelaySeconds = ComputeRetryDelaySeconds(
                            delivery.TryCount,
                            _options.BaseRetryDelaySeconds,
                            _options.MaxRetryDelaySeconds);
                        var nextAttempt = DateTime.UtcNow.AddSeconds(retryDelaySeconds);
                        delivery.MarkFailed(
                            NormalizeError(result.ErrorCode),
                            nextAttempt,
                            DateTime.UtcNow,
                            result.ErrorCode);
                    }
                    else
                    {
                        delivery.MarkDeadLettered(
                            NormalizeError(result.ErrorCode),
                            DateTime.UtcNow,
                            responseCode: result.ErrorCode);
                    }

                    return new FinalizedDelivery
                    {
                        DeliveryId = claimed.DeliveryId,
                        RealtimeSignal = null,
                        WillRetry = willRetry,
                        IsDeadLettered = !willRetry
                    };
                }

                return new FinalizedDelivery
                {
                    DeliveryId = claimed.DeliveryId,
                    RealtimeSignal = realtimeSignal,
                    WillRetry = false,
                    IsDeadLettered = false
                };
            },
            cancellationToken);

    private async Task PublishRealtimeSignalAsync(
        AppNotificationSignalDto realtimeSignal,
        CancellationToken cancellationToken)
    {
        try
        {
            await _realtimePublisher.PublishAppNotificationAsync(realtimeSignal, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to publish app notification realtime signal for NotificationId {NotificationId}.",
                realtimeSignal.NotificationId);
        }
    }

    private static bool IsDeliveryDue(NotificationDelivery delivery, DateTime utcNow, int maxRetryCount)
    {
        if (delivery.Status != NotificationStatus.Processing &&
            (delivery.Status != NotificationStatus.Queued &&
             delivery.Status != NotificationStatus.Failed ||
             delivery.TryCount >= maxRetryCount))
        {
            return false;
        }

        return !delivery.NextAttemptTimeUtc.HasValue || delivery.NextAttemptTimeUtc.Value <= utcNow;
    }

    private static int ComputeRetryDelaySeconds(int tryCount, int baseDelaySeconds, int maxDelaySeconds)
    {
        var baseDelay = Math.Clamp(baseDelaySeconds, 1, 3600);
        var maxDelay = Math.Clamp(maxDelaySeconds, baseDelay, 86400);
        var exponent = Math.Max(0, tryCount - 1);
        var multiplied = (long)baseDelay << Math.Min(exponent, 20);
        return (int)Math.Min(multiplied, maxDelay);
    }

    private static bool TryDeserializeTemplateModel(
        string templateModelJson,
        out Dictionary<string, string?> templateModel)
    {
        if (string.IsNullOrWhiteSpace(templateModelJson))
        {
            templateModel = new Dictionary<string, string?>(StringComparer.Ordinal);
            return true;
        }

        try
        {
            templateModel = JsonSerializer.Deserialize<Dictionary<string, string?>>(templateModelJson, JsonOptions)
                ?? new Dictionary<string, string?>(StringComparer.Ordinal);
            return true;
        }
        catch (JsonException)
        {
            templateModel = [];
            return false;
        }
    }

    private static string NormalizeError(string? errorCode)
    {
        if (string.IsNullOrWhiteSpace(errorCode))
        {
            return "delivery_failed";
        }

        var normalized = errorCode.Trim();
        return normalized.Length <= 128 ? normalized : normalized[..128];
    }

    private static string SanitizeException(Exception exception)
    {
        var error = exception switch
        {
            TimeoutException => "delivery_timeout",
            InvalidOperationException => "delivery_invalid_operation",
            _ => "delivery_failed"
        };

        return error;
    }

    private async Task TryPublishDeliveryAuditAsync(
        ClaimedDelivery claimed,
        ChannelDeliveryResult result,
        FinalizedDelivery finalized,
        double processDurationMs,
        CancellationToken cancellationToken)
    {
        var outcome = result.IsSuccess
            ? claimed.Notification.Channel == NotificationChannel.App ? "delivered" : "provider_accepted"
            : result.IsUnavailable
                ? "unavailable"
            : finalized.WillRetry
                ? "retry"
                : finalized.IsDeadLettered ? "dead_lettered" : "failed";

        try
        {
            await _auditLogPublisher.PublishAsync(
                new AuditLogWriteRequest
                {
                    EventId = claimed.DeliveryAttemptId,
                    TenantId = claimed.TenantId,
                    OccurredAtUtc = DateTime.UtcNow,
                    Category = "notifications.delivery.worker",
                    UserId = string.IsNullOrWhiteSpace(result.UserId) ? null : result.UserId,
                    StatusCode = result.IsSuccess
                        ? StatusCodes.Status200OK
                        : result.IsUnavailable
                            ? StatusCodes.Status503ServiceUnavailable
                            : StatusCodes.Status500InternalServerError,
                    DurationMs = processDurationMs,
                    Comments = result.IsSuccess
                        ? []
                        : [NormalizeError(result.ErrorCode)],
                    Exceptions = result.IsSuccess
                        ? []
                        :
                        [
                            new AuditLogWriteRequestException
                            {
                                Type = "notifications.delivery.failure",
                                Message = NormalizeError(result.ErrorCode)
                            }
                        ],
                    RedactionApplied = true,
                    RedactionVersion = 1,
                    Actions =
                    [
                        new AuditLogWriteRequestAction
                        {
                            Name = "notifications.delivery.process",
                            Result = outcome,
                            DurationMs = processDurationMs,
                            Parameters = new Dictionary<string, string?>(StringComparer.Ordinal)
                            {
                                ["notificationId"] = claimed.NotificationId.ToString("D"),
                                ["deliveryId"] = claimed.DeliveryId.ToString(CultureInfo.InvariantCulture),
                                ["channel"] = claimed.Notification.Channel.ToString(),
                                ["outcome"] = outcome,
                                ["willRetry"] = finalized.WillRetry.ToString(CultureInfo.InvariantCulture)
                            }
                        }
                    ]
                },
                cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to publish audit log for notification delivery {DeliveryId}.",
                claimed.DeliveryId);
        }
    }

    private enum DeliveryClaimDisposition
    {
        None = 0,
        Claimed = 1,
        NeedsReview = 2,
        QuietHoursHeld = 3,
        DeadLettered = 4
    }

    private sealed class DeliveryClaimResult
    {
        public static DeliveryClaimResult None { get; } = new();

        public DeliveryClaimDisposition Disposition { get; init; }
        public NotificationChannel? Channel { get; init; }
        public long DeliveryId { get; init; }
        public Guid? DeliveryAttemptId { get; init; }
        public bool PolicyHoldReleased { get; init; }
        public string? PolicyBypassReason { get; init; }
        public ClaimedDelivery? ClaimedDelivery { get; init; }
    }

    private sealed class ClaimedDelivery
    {
        public required long DeliveryId { get; init; }
        public required Guid DeliveryAttemptId { get; init; }
        public required Guid NotificationId { get; init; }
        public required Guid? TenantId { get; init; }
        public required Notification Notification { get; init; }
        public required NotificationRecipient Recipient { get; init; }
        public required IReadOnlyDictionary<string, string?> TemplateModel { get; init; }
        public required double QueueLagMs { get; init; }
    }

    private sealed record RenderedInboxContent(string? Title, string? Summary, string? Body)
    {
        public static RenderedInboxContent Empty { get; } = new(null, null, null);
    }

    private sealed class FinalizedDelivery
    {
        public required long DeliveryId { get; init; }
        public required AppNotificationSignalDto? RealtimeSignal { get; init; }
        public required bool WillRetry { get; init; }
        public required bool IsDeadLettered { get; init; }
    }
}
