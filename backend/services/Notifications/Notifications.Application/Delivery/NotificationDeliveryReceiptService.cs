using Microsoft.EntityFrameworkCore;
using Notifications.Application.Contracts.Delivery;
using Notifications.Domain.Shared.Enums;
using Notifications.EntityFrameworkCore.EntityFrameworkCore;
using Shared.Modularity.DependencyInjection;
using Shared.EntityFrameworkCore;
using Shared.EntityFrameworkCore.TenantDatabases;
using Notifications.Application.Dispatches;
namespace Notifications.Application.Delivery;

/// <summary>
/// Applies asynchronous provider delivery receipts (e.g. Twilio status callbacks). Runs host-context
/// from an anonymous webhook, so it queries with <see cref="EntityFrameworkQueryableExtensions.IgnoreQueryFilters{TEntity}"/>.
/// A provider-reported failure dead-letters a delivery we optimistically marked Sent; success/other
/// statuses are no-ops. Idempotent by construction.
/// </summary>
public sealed class NotificationDeliveryReceiptService : INotificationDeliveryReceiptService, ITransientDependency
{
    private readonly IDbContextProvider<NotificationsDbContextApp> _dbContextProvider;

    public NotificationDeliveryReceiptService(IDbContextProvider<NotificationsDbContextApp> dbContextProvider)
    {
        _dbContextProvider = dbContextProvider;
    }

    public async Task<bool> ApplyProviderStatusAsync(
        string providerMessageId,
        NotificationChannel channel,
        Guid authenticatedProviderConfigId,
        NotificationDeliveryReceiptStatus status,
        string? providerErrorCode,
        CancellationToken cancellationToken = default)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        if (string.IsNullOrWhiteSpace(providerMessageId) || authenticatedProviderConfigId == Guid.Empty)
        {
            return false;
        }

        var matches = await unitOfWorkDbContext.NotificationDeliveries
            .IgnoreQueryFilters()
            .WhereOwnedByCurrentDatabase()
            .Include(x => x.Notification)
            .Where(x => x.ProviderMessageId == providerMessageId && x.Notification.Channel == channel)
            .Take(2).ToListAsync(cancellationToken);

        if (matches.Count != 1 || matches[0].ProviderConfigId != authenticatedProviderConfigId)
        {
            return false;
        }
        var delivery = matches[0];

        providerErrorCode = Providers.NotificationHttpResponseOutcome.NumericProviderCode(providerErrorCode);
        var now = DateTime.UtcNow;
        var providerActor = $"{channel}.callback";
        var contradictoryReceipt = false;

        // Provider callbacks may be redelivered. Failed is terminal once the optimistic Sent state
        // changes, while delivered remains represented by Sent and therefore needs an explicit
        // chronology existence check to avoid appending duplicates.
        if (status == NotificationDeliveryReceiptStatus.Failed)
        {
            if (delivery.Status is not (NotificationStatus.ProviderAccepted or NotificationStatus.Delivered))
            {
                return true;
            }

            var reason = string.IsNullOrWhiteSpace(providerErrorCode)
                ? "provider_reported_failure"
                : $"provider_reported_failure:{providerErrorCode}";
            if (delivery.Status == NotificationStatus.Delivered)
            {
                contradictoryReceipt = true;
                delivery.MarkNeedsReview("contradictory_delivery_evidence", now, providerActor, NotificationDeliveryActorType.Provider);
            }
            else
                delivery.MarkDeadLettered(reason, now, providerActor, NotificationDeliveryActorType.Provider, providerErrorCode);
        }
        else
        {
            var deliveredAlreadyRecorded = await unitOfWorkDbContext.NotificationDeliveryChronology
                .IgnoreQueryFilters()
                .AnyAsync(
                    x => x.DeliveryId == delivery.Id &&
                         x.EventKind == NotificationDeliveryEventKind.ProviderDelivered,
                    cancellationToken);
            if (deliveredAlreadyRecorded)
            {
                return true;
            }

            if (delivery.Status == NotificationStatus.ProviderAccepted)
            {
                delivery.MarkDelivered(
                    now,
                    actorId: providerActor,
                    actorType: NotificationDeliveryActorType.Provider,
                    responseCode: providerErrorCode,
                    eventKind: NotificationDeliveryEventKind.ProviderDelivered);
            }
            else if (delivery.Status == NotificationStatus.DeadLettered)
            {
                contradictoryReceipt = true;
                delivery.RecordProviderFeedback(now, providerActor, providerErrorCode, "contradictory_delivery_evidence",
                    NotificationDeliveryEventKind.ProviderDelivered);
                delivery.MarkNeedsReview("contradictory_delivery_evidence", now, providerActor, NotificationDeliveryActorType.Provider);
            }
        }

        await NotificationDispatchGate.DirtyAsync(unitOfWorkDbContext, delivery.Notification, now, cancellationToken, contradictoryReceipt);
        await unitOfWorkDbContext.SaveChangesAsync(cancellationToken);
        return true;
    }
}
