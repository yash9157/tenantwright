using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Notifications.Application.Contracts.Delivery;
using Notifications.Application.Delivery;
using Notifications.Application.Delivery.Providers;
using Notifications.Application.Realtime;
using Notifications.Domain.Notifications;
using Notifications.Domain.Shared.Enums;
using Shared.AuditLogs.Publishing;
using Xunit;

namespace Notifications.Tests;

public sealed class NotificationProviderOutcomeTests
{
    [Theory]
    [InlineData(408, NotificationSendOutcome.Ambiguous)]
    [InlineData(500, NotificationSendOutcome.Ambiguous)]
    [InlineData(502, NotificationSendOutcome.Ambiguous)]
    [InlineData(429, NotificationSendOutcome.RetryableRejection)]
    [InlineData(400, NotificationSendOutcome.PermanentRejection)]
    [InlineData(410, NotificationSendOutcome.PermanentRejection)]
    public void Undocumented_server_outcomes_are_ambiguous_while_explicit_rejections_are_typed(int status, NotificationSendOutcome expected)
    {
        using var response = new HttpResponseMessage((HttpStatusCode)status);
        var result = NotificationHttpResponseOutcome.Rejection(response, "provider_status");
        Assert.Equal(expected, result.Outcome);
    }

    [Fact]
    public void Documented_backoff_and_retry_after_are_minima()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        var retryAfter = DateTimeOffset.UtcNow.AddHours(1);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(retryAfter);
        var result = NotificationHttpResponseOutcome.Rejection(response, "apns_503", TimeSpan.FromMinutes(15), documentedServerRejection: true);
        Assert.Equal(NotificationSendOutcome.RetryableRejection, result.Outcome);
        Assert.True(result.RetryAfterUtc >= retryAfter.UtcDateTime);
    }

    [Theory]
    [InlineData(NotificationSendOutcome.Accepted, NotificationStatus.ProviderAccepted)]
    [InlineData(NotificationSendOutcome.Delivered, NotificationStatus.Delivered)]
    [InlineData(NotificationSendOutcome.PermanentRejection, NotificationStatus.DeadLettered)]
    [InlineData(NotificationSendOutcome.RetryableRejection, NotificationStatus.Failed)]
    [InlineData(NotificationSendOutcome.Ambiguous, NotificationStatus.NeedsReview)]
    [InlineData(NotificationSendOutcome.UnavailableBeforeSend, NotificationStatus.NeedsReview)]
    public async Task Worker_retries_only_definitive_retryable_rejection(NotificationSendOutcome outcome, NotificationStatus expected)
    {
        var retryAt = DateTime.UtcNow.AddHours(1);
        var result = outcome switch
        {
            NotificationSendOutcome.Accepted => ChannelDeliveryResult.Success("accepted"),
            NotificationSendOutcome.Delivered => new ChannelDeliveryResult { Outcome = NotificationSendOutcome.Delivered, ProviderMessageId = "delivered" },
            NotificationSendOutcome.PermanentRejection => ChannelDeliveryResult.PermanentRejection("rejected"),
            NotificationSendOutcome.RetryableRejection => ChannelDeliveryResult.RetryableRejection("limited", retryAt),
            NotificationSendOutcome.UnavailableBeforeSend => ChannelDeliveryResult.Unavailable("not_configured"),
            _ => ChannelDeliveryResult.Ambiguous("response_lost")
        };
        await AssertWorkerOutcomeAsync((_, _) => Task.FromResult(result), expected, retryAt);
    }

    [Fact]
    public Task Unknown_network_exception_requires_review_without_automatic_resend() =>
        AssertWorkerOutcomeAsync((_, _) => throw new HttpRequestException("Response lost after sending"), NotificationStatus.NeedsReview);

    [Fact]
    public Task An_invalid_provider_outcome_never_implies_a_definitive_rejection() =>
        AssertWorkerOutcomeAsync((_, _) => Task.FromResult(new ChannelDeliveryResult { Outcome = (NotificationSendOutcome)999 }), NotificationStatus.NeedsReview);

    [Fact]
    public Task Provider_deadline_requires_review_without_waiting_indefinitely() =>
        AssertWorkerOutcomeAsync(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return ChannelDeliveryResult.Success();
        }, NotificationStatus.NeedsReview);

    [Fact]
    public async Task Provider_message_collision_across_tenants_cannot_select_the_first_delivery()
    {
        await using var context = NotificationsEfCoreTestHarness.BuildDbContext(Guid.NewGuid().ToString(), null);
        var authenticatedConfigId = Guid.NewGuid();
        for (var ordinal = 1; ordinal <= 2; ordinal++)
        {
            var tenantId = Guid.NewGuid();
            var notification = new Notification(Guid.NewGuid(), tenantId, Guid.NewGuid(), NotificationChannel.Sms, "test", "{}");
            var recipient = new NotificationRecipient(ordinal, tenantId, notification.Id, NotificationRecipientType.PhoneNumber, "+12025550100");
            var delivery = new NotificationDelivery(ordinal, tenantId, notification.Id, recipient.Id);
            delivery.MarkProviderAccepted(DateTime.UtcNow, "colliding-provider-id", authenticatedConfigId);
            context.Notifications.Add(notification);
            context.NotificationRecipients.Add(recipient);
            context.NotificationDeliveries.Add(delivery);
        }
        await context.SaveChangesAsync();
        var receipts = new NotificationDeliveryReceiptService(NotificationsEfCoreTestHarness.AsDbContextProvider(context));
        Assert.False(await receipts.ApplyProviderStatusAsync("colliding-provider-id", NotificationChannel.Sms,
            authenticatedConfigId, NotificationDeliveryReceiptStatus.Failed, null));
        Assert.All(context.NotificationDeliveries, delivery => Assert.Equal(NotificationStatus.ProviderAccepted, delivery.Status));
    }

    [Fact]
    public async Task Receipt_authenticated_by_another_configuration_cannot_advance_a_delivery()
    {
        await using var context = NotificationsEfCoreTestHarness.BuildDbContext(Guid.NewGuid().ToString(), null);
        var tenantId = Guid.NewGuid();
        var notification = new Notification(Guid.NewGuid(), tenantId, Guid.NewGuid(), NotificationChannel.WhatsApp, "test", "{}");
        var recipient = new NotificationRecipient(1, tenantId, notification.Id, NotificationRecipientType.PhoneNumber, "+12025550100");
        var delivery = new NotificationDelivery(1, tenantId, notification.Id, recipient.Id);
        var providerConfigId = Guid.NewGuid();
        delivery.MarkProviderAccepted(DateTime.UtcNow, "provider-message", providerConfigId);
        context.AddRange(notification, recipient, delivery);
        await context.SaveChangesAsync();
        var receipts = new NotificationDeliveryReceiptService(NotificationsEfCoreTestHarness.AsDbContextProvider(context));

        Assert.False(await receipts.ApplyProviderStatusAsync("provider-message", NotificationChannel.WhatsApp,
            Guid.NewGuid(), NotificationDeliveryReceiptStatus.Failed, null));
        Assert.Equal(NotificationStatus.ProviderAccepted, delivery.Status);
        Assert.True(await receipts.ApplyProviderStatusAsync("provider-message", NotificationChannel.WhatsApp,
            providerConfigId, NotificationDeliveryReceiptStatus.Failed, null));
        Assert.Equal(NotificationStatus.DeadLettered, delivery.Status);
    }

    private static async Task AssertWorkerOutcomeAsync(Func<ChannelDeliveryContext, CancellationToken, Task<ChannelDeliveryResult>> send,
        NotificationStatus expected, DateTime? minimumRetryAtUtc = null)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        var tenantId = Guid.NewGuid();
        await using var context = NotificationsEfCoreTestHarness.BuildSqliteDbContext(connection, tenantId);
        await context.Database.EnsureCreatedAsync();
        var notification = new Notification(Guid.NewGuid(), tenantId, Guid.NewGuid(), NotificationChannel.Email, "test", "{}");
        var recipient = new NotificationRecipient(0, tenantId, notification.Id, NotificationRecipientType.EmailAddress, "user@example.test");
        context.Notifications.Add(notification);
        context.NotificationRecipients.Add(recipient);
        await context.SaveChangesAsync();
        var delivery = new NotificationDelivery(0, tenantId, notification.Id, recipient.Id);
        context.NotificationDeliveries.Add(delivery);
        await context.SaveChangesAsync();
        var handler = new OutcomeHandler(send);
        var worker = new NotificationDeliveryWorker(new RelationalUnitOfWorkExecutor(context), NotificationsEfCoreTestHarness.AsDbContextProvider(context),
            [handler], new NoopNotificationsRealtimePublisher(), new SilentDispatchAuditPublisher(), new TestCurrentTenant(tenantId),
            new StubNotificationsTemplateRenderer(), Options.Create(new NotificationsDeliveryOptions { ProviderTimeoutSeconds = 1 }),
            NullLogger<NotificationDeliveryWorker>.Instance);
        Assert.Equal(1, await worker.ProcessBatchAsync());
        Assert.Equal(expected, delivery.Status);
        if (expected == NotificationStatus.Failed)
            Assert.True(delivery.NextAttemptTimeUtc >= minimumRetryAtUtc);
        else
            Assert.Null(delivery.NextAttemptTimeUtc);
        Assert.Equal(0, await worker.ProcessBatchAsync());
        Assert.Equal(1, handler.CallCount);
    }

    private sealed class OutcomeHandler(Func<ChannelDeliveryContext, CancellationToken, Task<ChannelDeliveryResult>> send) : IChannelDeliveryHandler
    {
        public int CallCount { get; private set; }
        public NotificationChannel Channel => NotificationChannel.Email;
        public Task<ChannelDeliveryResult> DeliverAsync(ChannelDeliveryContext context, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return send(context, cancellationToken);
        }
    }
}

internal sealed class SilentDispatchAuditPublisher : IAuditLogWriteRequestPublisher
{
    public Task PublishAsync(AuditLogWriteRequest request, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
