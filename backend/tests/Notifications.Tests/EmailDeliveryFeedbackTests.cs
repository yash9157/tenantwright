using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Notifications.Application.Contracts.Delivery;
using Notifications.Application.Delivery;
using Notifications.Application.Delivery.Providers;
using Notifications.Domain.Notifications;
using Notifications.Domain.Shared.Enums;
using Xunit;

namespace Notifications.Tests;

/// <summary>
/// Coverage for the email delivery-feedback foundation: the generic HMAC signature validator, the
/// generic JSON event parser, and the idempotent recording service (delivered vs bounce/complaint
/// state transitions, duplicates, and unmatched message ids).
/// </summary>
public class EmailDeliveryFeedbackTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private const string Provider = "genericmail";

    // ---- HMAC signature validator ----

    [Fact]
    public void Hmac_validator_accepts_a_correct_signature_with_fresh_timestamp()
    {
        var options = Options.Create(new NotificationsEmailFeedbackOptions { SigningSecret = "secret", Enabled = true });
        var validator = new HmacEmailEventSignatureValidator(options);
        var body = "{\"ok\":true}";
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);

        var request = Request(body, new()
        {
            ["X-Signature"] = Hmac("secret", $"{ts}.{body}"),
            ["X-Timestamp"] = ts
        });

        Assert.True(validator.IsValid(request));
    }

    [Fact]
    public void Hmac_validator_rejects_a_wrong_signature()
    {
        var options = Options.Create(new NotificationsEmailFeedbackOptions { SigningSecret = "secret" });
        var validator = new HmacEmailEventSignatureValidator(options);
        var body = "{\"ok\":true}";
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);

        var request = Request(body, new()
        {
            ["X-Signature"] = Hmac("WRONG", $"{ts}.{body}"),
            ["X-Timestamp"] = ts
        });

        Assert.False(validator.IsValid(request));
    }

    [Fact]
    public void Hmac_validator_rejects_a_stale_timestamp()
    {
        var options = Options.Create(new NotificationsEmailFeedbackOptions { SigningSecret = "secret", TimestampToleranceSeconds = 60 });
        var validator = new HmacEmailEventSignatureValidator(options);
        var body = "{\"ok\":true}";
        var staleTs = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);

        var request = Request(body, new()
        {
            ["X-Signature"] = Hmac("secret", $"{staleTs}.{body}"),
            ["X-Timestamp"] = staleTs
        });

        Assert.False(validator.IsValid(request));
    }

    [Fact]
    public void Hmac_validator_rejects_when_no_secret_is_configured()
    {
        var validator = new HmacEmailEventSignatureValidator(Options.Create(new NotificationsEmailFeedbackOptions()));
        var request = Request("{}", new() { ["X-Signature"] = "abc" });
        Assert.False(validator.IsValid(request));
    }

    // ---- Generic JSON parser ----

    [Fact]
    public void Parser_maps_event_types_and_skips_unrecognized()
    {
        var parser = new GenericJsonEmailEventParser();
        var body = """
        [
          { "id": "e1", "messageId": "m1", "type": "delivered" },
          { "id": "e2", "messageId": "m2", "type": "hard_bounce", "code": "5.1.1", "reason": "no mailbox" },
          { "id": "e3", "messageId": "m3", "type": "complaint" },
          { "id": "e4", "messageId": "m4", "type": "open" },
          { "messageId": "m5", "type": "delivered" }
        ]
        """;

        var events = parser.Parse(Request(body, new()));

        Assert.Equal(3, events.Count); // "open" and the id-less row are skipped
        Assert.Equal(EmailDeliveryEventType.Delivered, events[0].EventType);
        Assert.Equal(EmailDeliveryEventType.Bounce, events[1].EventType);
        Assert.Equal("5.1.1", events[1].ProviderCode);
        Assert.Equal(EmailDeliveryEventType.Complaint, events[2].EventType);
    }

    // ---- Recording service ----

    [Fact]
    public async Task Bounce_records_a_receipt_and_dead_letters_a_sent_delivery()
    {
        using var db = BuildDb();
        await SeedSentEmailDeliveryAsync(db, deliveryId: 1, providerMessageId: "pm-1");
        var service = new EmailDeliveryFeedbackService(NotificationsEfCoreTestHarness.AsDbContextProvider(db));

        var result = await service.RecordAsync(Provider, new EmailDeliveryEvent
        {
            ProviderEventId = "evt-1",
            ProviderMessageId = "pm-1",
            EventType = EmailDeliveryEventType.Bounce,
            ProviderCode = "5.1.1"
        });

        Assert.Equal(EmailFeedbackRecordResult.Accepted, result);
        var receipt = Assert.Single(db.NotificationDeliveryReceipts.ToList());
        Assert.Equal(EmailDeliveryEventType.Bounce, receipt.EventType);
        var delivery = db.NotificationDeliveries.Single(x => x.Id == 1);
        Assert.Equal(NotificationStatus.DeadLettered, delivery.Status);
    }

    [Fact]
    public async Task Delivered_records_a_receipt_and_confirms_final_delivery()
    {
        using var db = BuildDb();
        await SeedSentEmailDeliveryAsync(db, deliveryId: 2, providerMessageId: "pm-2");
        var service = new EmailDeliveryFeedbackService(NotificationsEfCoreTestHarness.AsDbContextProvider(db));

        var result = await service.RecordAsync(Provider, new EmailDeliveryEvent
        {
            ProviderEventId = "evt-2",
            ProviderMessageId = "pm-2",
            EventType = EmailDeliveryEventType.Delivered
        });

        Assert.Equal(EmailFeedbackRecordResult.Accepted, result);
        Assert.Equal(NotificationStatus.Delivered, db.NotificationDeliveries.Single(x => x.Id == 2).Status);
    }

    [Fact]
    public async Task Duplicate_event_is_a_no_op()
    {
        using var db = BuildDb();
        await SeedSentEmailDeliveryAsync(db, deliveryId: 3, providerMessageId: "pm-3");
        var service = new EmailDeliveryFeedbackService(NotificationsEfCoreTestHarness.AsDbContextProvider(db));
        var evt = new EmailDeliveryEvent
        {
            ProviderEventId = "evt-3",
            ProviderMessageId = "pm-3",
            EventType = EmailDeliveryEventType.Delivered
        };

        Assert.Equal(EmailFeedbackRecordResult.Accepted, await service.RecordAsync(Provider, evt));
        Assert.Equal(EmailFeedbackRecordResult.Duplicate, await service.RecordAsync(Provider, evt));
        Assert.Single(db.NotificationDeliveryReceipts.ToList());
        Assert.Single(db.NotificationDeliveryChronology.Where(x =>
            x.DeliveryId == 3 && x.EventKind == NotificationDeliveryEventKind.ProviderDelivered));
    }

    [Fact]
    public async Task Duplicate_failed_event_appends_provider_feedback_and_terminal_transition_once()
    {
        using var db = BuildDb();
        await SeedSentEmailDeliveryAsync(db, deliveryId: 4, providerMessageId: "pm-4");
        var service = new EmailDeliveryFeedbackService(NotificationsEfCoreTestHarness.AsDbContextProvider(db));
        var evt = new EmailDeliveryEvent
        {
            ProviderEventId = "evt-4",
            ProviderMessageId = "pm-4",
            EventType = EmailDeliveryEventType.Bounce,
            ProviderCode = "5.1.1",
            SafeReason = "mailbox_rejected"
        };

        Assert.Equal(EmailFeedbackRecordResult.Accepted, await service.RecordAsync(Provider, evt));
        Assert.Equal(EmailFeedbackRecordResult.Duplicate, await service.RecordAsync(Provider, evt));

        Assert.Single(db.NotificationDeliveryReceipts.Where(x => x.DeliveryId == 4));
        Assert.Single(db.NotificationDeliveryChronology.Where(x =>
            x.DeliveryId == 4 && x.EventKind == NotificationDeliveryEventKind.ProviderFeedback));
        Assert.Single(db.NotificationDeliveryChronology.Where(x =>
            x.DeliveryId == 4 && x.EventKind == NotificationDeliveryEventKind.DeadLettered));
    }

    [Fact]
    public async Task Unmatched_message_id_records_nothing()
    {
        using var db = BuildDb();
        var service = new EmailDeliveryFeedbackService(NotificationsEfCoreTestHarness.AsDbContextProvider(db));

        var result = await service.RecordAsync(Provider, new EmailDeliveryEvent
        {
            ProviderEventId = "evt-x",
            ProviderMessageId = "does-not-exist",
            EventType = EmailDeliveryEventType.Bounce
        });

        Assert.Equal(EmailFeedbackRecordResult.Unmatched, result);
        Assert.Empty(db.NotificationDeliveryReceipts.ToList());
    }

    // ---- helpers ----

    private static Notifications.EntityFrameworkCore.EntityFrameworkCore.NotificationsDbContextApp BuildDb()
        => NotificationsEfCoreTestHarness.BuildDbContext(Guid.NewGuid().ToString(), tenantId: null, multiTenantFilterEnabled: false);

    private static async Task SeedSentEmailDeliveryAsync(
        Notifications.EntityFrameworkCore.EntityFrameworkCore.NotificationsDbContextApp db,
        long deliveryId,
        string providerMessageId)
    {
        var notification = new Notification(
            id: Guid.NewGuid(),
            tenantId: Tenant,
            requestId: Guid.NewGuid(),
            channel: NotificationChannel.Email,
            templateName: "Tpl",
            templateModelJson: "{}",
            subject: "S");
        db.Notifications.Add(notification);

        var delivery = new NotificationDelivery(deliveryId, Tenant, notification.Id, recipientId: deliveryId);
        delivery.MarkProcessing(DateTime.UtcNow.AddMinutes(-2), DateTime.UtcNow.AddMinutes(-1));
        delivery.MarkProviderAccepted(DateTime.UtcNow.AddMinutes(-1), providerMessageId);
        db.NotificationDeliveries.Add(delivery);

        await db.SaveChangesAsync();
    }

    private static EmailWebhookRequest Request(string body, Dictionary<string, string> headers)
        => new()
        {
            Provider = Provider,
            RawBody = body,
            Headers = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase)
        };

    private static string Hmac(string secret, string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
    }
}
