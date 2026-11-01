using Microsoft.EntityFrameworkCore;
using Notifications.Application.Contracts.Delivery;
using Notifications.Domain.Notifications;
using Notifications.Domain.Shared.Enums;
using Notifications.EntityFrameworkCore.EntityFrameworkCore;
using Shared.Modularity.DependencyInjection;
using Shared.EntityFrameworkCore;
namespace Notifications.Application.Delivery;

/// <summary>
/// Records asynchronous email delivery-feedback facts (delivered/bounce/complaint) against the
/// matching email delivery. Runs host-context from an anonymous, signature-verified webhook, so it
/// queries with <see cref="EntityFrameworkQueryableExtensions.IgnoreQueryFilters{TEntity}"/> and
/// derives tenant/config strictly from the stored delivery (never from the webhook body). Idempotent
/// by (provider, provider event id). A hard bounce/complaint dead-letters a previously Sent delivery;
/// a delivered event is informational.
/// </summary>
public sealed class EmailDeliveryFeedbackService : IEmailDeliveryFeedbackService, ITransientDependency
{
    private readonly IDbContextProvider<NotificationsDbContextApp> _dbContextProvider;

    public EmailDeliveryFeedbackService(IDbContextProvider<NotificationsDbContextApp> dbContextProvider)
    {
        _dbContextProvider = dbContextProvider;
    }

    public async Task<EmailFeedbackRecordResult> RecordAsync(
        string providerName,
        EmailDeliveryEvent feedbackEvent,
        CancellationToken cancellationToken = default)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        if (string.IsNullOrWhiteSpace(providerName) ||
                    feedbackEvent is null ||
                    string.IsNullOrWhiteSpace(feedbackEvent.ProviderEventId) ||
                    string.IsNullOrWhiteSpace(feedbackEvent.ProviderMessageId))
        {
            return EmailFeedbackRecordResult.Unmatched;
        }

        var normalizedProvider = providerName.Trim();

        // Idempotency: a duplicate provider event is a no-op.
        var alreadyRecorded = await unitOfWorkDbContext.NotificationDeliveryReceipts
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(
                x => x.ProviderName == normalizedProvider && x.ProviderEventId == feedbackEvent.ProviderEventId,
                cancellationToken);
        if (alreadyRecorded)
        {
            return EmailFeedbackRecordResult.Duplicate;
        }

        var delivery = await unitOfWorkDbContext.NotificationDeliveries
            .IgnoreQueryFilters()
            .Include(x => x.Notification)
            .FirstOrDefaultAsync(
                x => x.ProviderMessageId == feedbackEvent.ProviderMessageId &&
                     x.Notification.Channel == NotificationChannel.Email,
                cancellationToken);
        if (delivery == null)
        {
            return EmailFeedbackRecordResult.Unmatched;
        }

        var now = DateTime.UtcNow;
        var receipt = new NotificationDeliveryReceipt(
            id: 0,
            tenantId: delivery.TenantId,
            deliveryId: delivery.Id,
            providerConfigId: delivery.ProviderConfigId,
            providerName: normalizedProvider,
            providerEventId: feedbackEvent.ProviderEventId,
            eventType: feedbackEvent.EventType,
            providerCode: feedbackEvent.ProviderCode,
            safeReason: feedbackEvent.SafeReason,
            occurredAtUtc: feedbackEvent.OccurredAtUtc ?? now,
            receivedAtUtc: now);

        await unitOfWorkDbContext.AddDeliveryReceiptAsync(receipt, cancellationToken);

        var feedbackOccurredAtUtc = feedbackEvent.OccurredAtUtc ?? now;
        if (feedbackEvent.EventType == EmailDeliveryEventType.Delivered &&
            delivery.Status == NotificationStatus.ProviderAccepted)
        {
            delivery.MarkDelivered(
                feedbackOccurredAtUtc,
                actorId: normalizedProvider,
                actorType: NotificationDeliveryActorType.Provider,
                responseCode: feedbackEvent.ProviderCode,
                eventKind: NotificationDeliveryEventKind.ProviderDelivered);
        }
        else
        {
            delivery.RecordProviderFeedback(
                feedbackOccurredAtUtc,
                normalizedProvider,
                feedbackEvent.ProviderCode,
                feedbackEvent.SafeReason);
        }

        // A hard bounce or complaint invalidates an externally accepted delivery.
        if (feedbackEvent.EventType is EmailDeliveryEventType.Bounce or EmailDeliveryEventType.Complaint &&
            delivery.Status is NotificationStatus.ProviderAccepted or NotificationStatus.Delivered)
        {
            delivery.MarkDeadLettered(
                $"email_{feedbackEvent.EventType.ToString().ToLowerInvariant()}",
                now,
                normalizedProvider,
                NotificationDeliveryActorType.Provider,
                feedbackEvent.ProviderCode);
        }

        await unitOfWorkDbContext.SaveChangesAsync(cancellationToken);
        return EmailFeedbackRecordResult.Accepted;
    }
}
