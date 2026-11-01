using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Notifications.Domain.Archival;
using Notifications.Domain.Notifications;
using Notifications.Domain.Shared.Enums;
using Notifications.EntityFrameworkCore.EntityFrameworkCore;
using Shared.EntityFrameworkCore;
using Shared.EntityFrameworkCore.TenantDatabases;
using Shared.Uow.Abstractions;
namespace Notifications.Application.Retention;

/// <summary>
/// Moves completed notification aggregates out of hot tables after the approved retention window,
/// then purges expired ephemeral presence observations. An archive envelope is durably saved before
/// the hot aggregate is deleted, so interruption can only leave a safe duplicate, never data loss.
/// </summary>
public sealed class NotificationRetentionService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDbContextProvider<NotificationsDbContextApp> _dbContextProvider;
    private readonly IUnitOfWorkExecutor _unitOfWorkExecutor;
    private readonly IOptionsMonitor<NotificationsRetentionOptions> _options;
    private readonly ILogger<NotificationRetentionService> _logger;

    public NotificationRetentionService(
        IDbContextProvider<NotificationsDbContextApp> dbContextProvider,
        IUnitOfWorkExecutor unitOfWorkExecutor,
        IOptionsMonitor<NotificationsRetentionOptions> options,
        ILogger<NotificationRetentionService> logger)
    {
        _dbContextProvider = dbContextProvider;
        _unitOfWorkExecutor = unitOfWorkExecutor;
        _options = options;
        _logger = logger;
    }

    public async Task<NotificationRetentionResult> PurgeAsync(CancellationToken cancellationToken = default)
        => await _unitOfWorkExecutor.ExecuteTransactionalAsync(
            _ => PurgeCoreAsync(cancellationToken),
            cancellationToken);

    private async Task<NotificationRetentionResult> PurgeCoreAsync(CancellationToken cancellationToken)
    {
        var unitOfWorkDbContext = await _dbContextProvider.GetDbContextAsync();
        var options = _options.CurrentValue;
        if (!options.Enabled)
        {
            return NotificationRetentionResult.Empty;
        }

        var now = DateTime.UtcNow;
        var batchSize = Math.Clamp(options.BatchSize, 1, 1000);
        var hotCutoff = now.AddDays(-Math.Max(365, options.HotRetentionDays));
        var presenceCutoff = now.AddDays(-Math.Max(90, options.PresenceRetentionDays));

        // Runs once per database (fan-out): only rows the visited database owns are purged, so rows a tenant left in the
        // shared database when it moved to a dedicated one stay untouched.
        var candidateIds = await unitOfWorkDbContext.Notifications
            .IgnoreQueryFilters()
            .WhereOwnedByCurrentDatabase()
            .Where(x => x.CreationTime <= hotCutoff)
            .Where(x => !unitOfWorkDbContext.NotificationDeliveries.IgnoreQueryFilters().Any(delivery =>
                delivery.NotificationId == x.Id &&
                (delivery.Status == NotificationStatus.Queued ||
                 delivery.Status == NotificationStatus.Processing ||
                 delivery.Status == NotificationStatus.Failed ||
                 delivery.Status == NotificationStatus.NeedsReview)))
            .OrderBy(x => x.CreationTime)
            .ThenBy(x => x.Id)
            .Select(x => x.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        var notifications = candidateIds.Count == 0
            ? []
            : await unitOfWorkDbContext.Notifications
                .IgnoreQueryFilters()
                .AsNoTracking()
                .AsSplitQuery()
                .Include(x => x.Recipients)
                .Include(x => x.Deliveries)
                    .ThenInclude(x => x.Chronology)
                .Include(x => x.UserNotifications)
                .Where(x => candidateIds.Contains(x.Id))
                .OrderBy(x => x.CreationTime)
                .ThenBy(x => x.Id)
                .ToListAsync(cancellationToken);
        var deliveryIds = notifications.SelectMany(x => x.Deliveries).Select(x => x.Id).ToArray();
        var receiptsByDelivery = deliveryIds.Length == 0
            ? Array.Empty<NotificationDeliveryReceipt>().ToLookup(x => x.DeliveryId)
            : (await unitOfWorkDbContext.NotificationDeliveryReceipts
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(x => deliveryIds.Contains(x.DeliveryId))
                .OrderBy(x => x.ReceivedAtUtc)
                .ThenBy(x => x.Id)
                .ToListAsync(cancellationToken))
                .ToLookup(x => x.DeliveryId);
        var notificationIds = notifications.Select(x => x.Id).ToArray();
        var alreadyArchived = notificationIds.Length == 0
            ? []
            : (await unitOfWorkDbContext.ArchivedNotifications
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(x => notificationIds.Contains(x.SourceNotificationId))
                .Select(x => x.SourceNotificationId)
                .ToListAsync(cancellationToken))
                .ToHashSet();

        foreach (var notification in notifications)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!alreadyArchived.Contains(notification.Id))
            {
                var payload = JsonSerializer.Serialize(new
                {
                    notification.Id,
                    notification.TenantId,
                    notification.RequestId,
                    notification.Channel,
                    notification.TemplateName,
                    notification.TemplateModelJson,
                    notification.Subject,
                    notification.Category,
                    notification.Priority,
                    notification.ActionUrl,
                    notification.ScheduledForUtc,
                    notification.PreferenceUserId,
                    notification.CreationTime,
                    Recipients = notification.Recipients.Select(recipient => new
                    {
                        recipient.Id,
                        recipient.RecipientType,
                        recipient.RecipientValue
                    }),
                    Deliveries = notification.Deliveries.Select(delivery => new
                    {
                        delivery.Id,
                        delivery.RecipientId,
                        delivery.Status,
                        delivery.ProviderMessageId,
                        delivery.ProviderConfigId,
                        delivery.LastError,
                        delivery.TryCount,
                        delivery.LastTryTimeUtc,
                        delivery.SentTimeUtc,
                        delivery.NotBeforeUtc,
                        delivery.CreationTime,
                        Chronology = delivery.Chronology.Select(item => new
                        {
                            item.EventKind,
                            item.Status,
                            item.ProviderMessageId,
                            item.ResponseCode,
                            item.SafeError,
                            item.LatencyMs,
                            item.ActorId,
                            item.ActorType,
                            item.OccurredAtUtc
                        }),
                        Receipts = receiptsByDelivery[delivery.Id].Select(receipt => new
                        {
                            receipt.ProviderConfigId,
                            receipt.ProviderName,
                            receipt.ProviderEventId,
                            receipt.EventType,
                            receipt.ProviderCode,
                            receipt.SafeReason,
                            receipt.OccurredAtUtc,
                            receipt.ReceivedAtUtc
                        })
                    }),
                    Inbox = notification.UserNotifications.Select(inbox => new
                    {
                        inbox.Id,
                        inbox.UserId,
                        inbox.IsRead,
                        inbox.ReadTimeUtc,
                        inbox.IsArchived,
                        inbox.ArchivedTimeUtc,
                        inbox.Title,
                        inbox.Summary,
                        inbox.Body,
                        inbox.CreationTime
                    })
                }, JsonOptions);
                var checksum = ComputePayloadSha256(payload);
                unitOfWorkDbContext.ArchivedNotifications.Add(new ArchivedNotification(
                    Guid.CreateVersion7(),
                    notification.TenantId,
                    notification.Id,
                    notification.CreationTime,
                    now,
                    payload,
                    checksum));
            }
        }
        if (unitOfWorkDbContext.ChangeTracker.HasChanges())
        {
            await unitOfWorkDbContext.SaveChangesAsync(cancellationToken);
        }

        var archived = notificationIds.Length == 0
            ? 0
            : await unitOfWorkDbContext.Notifications
                .IgnoreQueryFilters()
                .Where(x => notificationIds.Contains(x.Id))
                .ExecuteDeleteAsync(cancellationToken);

        var presenceIds = await unitOfWorkDbContext.NotificationPresenceRecords
            .IgnoreQueryFilters()
            .Where(x => x.LastSeenAtUtc <= presenceCutoff)
            .OrderBy(x => x.LastSeenAtUtc)
            .ThenBy(x => x.Id)
            .Select(x => x.Id)
            .Take(batchSize)
            .ToArrayAsync(cancellationToken);
        var purgedPresence = presenceIds.Length == 0
            ? 0
            : await unitOfWorkDbContext.NotificationPresenceRecords
                .IgnoreQueryFilters()
                .Where(x => presenceIds.Contains(x.Id))
                .ExecuteDeleteAsync(cancellationToken);

        var result = new NotificationRetentionResult(archived, purgedPresence);
        if (result.Total > 0)
        {
            _logger.LogInformation(
                "Notifications retention archived {ArchivedNotificationCount} completed notifications and purged {PresenceRecordCount} expired presence records.",
                result.ArchivedNotifications,
                result.PurgedPresenceRecords);
        }

        return result;
    }

    private static string ComputePayloadSha256(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        using var canonicalPayload = new MemoryStream();
        using (var writer = new Utf8JsonWriter(canonicalPayload))
        {
            WriteCanonicalJson(document.RootElement, writer);
        }

        return Convert.ToHexString(SHA256.HashData(canonicalPayload.ToArray())).ToLowerInvariant();
    }

    private static void WriteCanonicalJson(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(property.Value, writer);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonicalJson(item, writer);
                }
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
