using Notifications.Application.Contracts.Channels;
using Notifications.Application.Contracts.Delivery;
using Notifications.Application.Contracts.Emailing;
using Notifications.Application.Contracts.Templates;
using Notifications.Application.Delivery;
using Notifications.Application.Emailing;
using Notifications.Domain.Notifications;
using Notifications.Domain.Shared.Constants;
using Notifications.Domain.Shared.Enums;
using MailKit.Security;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using Xunit;

namespace Notifications.Tests;

public class NotificationDeliverySafetyTests
{
    [Fact]
    public void Processing_should_record_a_durable_attempt_identifier()
    {
        var delivery = CreateDelivery();
        var attemptId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        delivery.MarkProcessing(now, now.AddMinutes(5), attemptId);

        Assert.Equal(NotificationStatus.Processing, delivery.Status);
        Assert.Equal(attemptId, delivery.DeliveryAttemptId);
        Assert.Equal(now, delivery.DeliveryAttemptedAtUtc);
    }

    [Fact]
    public void Ambiguous_delivery_should_require_review_until_an_operator_retries_it()
    {
        var delivery = CreateDelivery();
        var now = DateTime.UtcNow;
        delivery.MarkProcessing(now, now.AddMinutes(5), Guid.NewGuid());

        delivery.MarkNeedsReview("provider_outcome_ambiguous", now.AddMinutes(6));

        Assert.Equal(NotificationStatus.NeedsReview, delivery.Status);
        Assert.Null(delivery.NextAttemptTimeUtc);

        delivery.MarkQueuedForOperatorRetry(now.AddMinutes(7));

        Assert.Equal(NotificationStatus.Queued, delivery.Status);
        Assert.Equal(now.AddMinutes(7), delivery.NextAttemptTimeUtc);
    }

    [Fact]
    public void Exhausted_delivery_should_be_dead_lettered_but_allow_explicit_operator_retry()
    {
        var delivery = CreateDelivery();
        var now = DateTime.UtcNow;
        delivery.MarkProcessing(now, now.AddMinutes(5), Guid.NewGuid());

        delivery.MarkDeadLettered("retry_limit_exhausted", now.AddMinutes(6));

        Assert.Equal(NotificationStatus.DeadLettered, delivery.Status);
        Assert.Null(delivery.NextAttemptTimeUtc);

        delivery.MarkQueuedForOperatorRetry(now.AddMinutes(7));

        Assert.Equal(NotificationStatus.Queued, delivery.Status);
        Assert.Equal(now.AddMinutes(7), delivery.NextAttemptTimeUtc);
    }

    [Fact]
    public async Task Email_delivery_should_html_encode_all_model_derived_values()
    {
        var renderer = new CapturingTemplateRenderer();
        var sender = new CapturingEmailSender();
        var handler = new EmailChannelDeliveryHandler(renderer, sender);

        var result = await handler.DeliverAsync(new ChannelDeliveryContext
        {
            TenantId = Guid.NewGuid(),
            NotificationId = Guid.NewGuid(),
            DeliveryAttemptId = Guid.NewGuid(),
            TemplateName = "Security.Alert",
            Subject = "Security alert",
            RecipientType = NotificationRecipientType.EmailAddress,
            RecipientValue = "user+<tag>@example.test",
            TemplateModel = new Dictionary<string, string?>
            {
                ["displayName"] = "<script>alert('x')</script>",
                ["nullable"] = null
            }
        });

        Assert.True(result.IsSuccess);
        Assert.Equal("&lt;script&gt;alert(&#39;x&#39;)&lt;/script&gt;", renderer.Model["displayName"]);
        Assert.Equal("user+&lt;tag&gt;@example.test", renderer.Model["recipient"]);
        Assert.Null(renderer.Model["nullable"]);
    }

    [Fact]
    public async Task Smtp_acceptance_should_remain_successful_when_disconnect_fails()
    {
        var transport = new AcceptedThenDisconnectFailsTransport();
        var dispatcher = new SmtpMessageDispatcher(
            new StubSmtpTransportFactory(transport),
            NullLogger<SmtpMessageDispatcher>.Instance);

        var response = await dispatcher.SendAsync(
            "smtp.tenantwright.local",
            587,
            SecureSocketOptions.StartTls,
            "user",
            "password",
            new MimeMessage());

        Assert.Equal("provider-accepted", response);
        Assert.True(transport.DisconnectAttempted);
    }

    private static NotificationDelivery CreateDelivery()
    {
        return new NotificationDelivery(
            id: 1,
            tenantId: Guid.NewGuid(),
            notificationId: Guid.NewGuid(),
            recipientId: 2);
    }

    private sealed class CapturingTemplateRenderer : INotificationsTemplateRenderer
    {
        public Dictionary<string, string?> Model { get; private set; } = [];

        public Task<RenderedNotificationTemplate> RenderAsync(
            string templateName,
            Dictionary<string, string?> model,
            CancellationToken cancellationToken = default)
            => RenderAsync(templateName, model, applyLayout: true, cancellationToken);

        public Task<RenderedNotificationTemplate> RenderAsync(
            string templateName,
            Dictionary<string, string?> model,
            bool applyLayout,
            CancellationToken cancellationToken = default)
        {
            Model = model;
            return Task.FromResult(new RenderedNotificationTemplate
            {
                Content = "<html>rendered</html>",
                Subject = "rendered"
            });
        }

        public Task<RenderedNotificationTemplate> RenderDraftAsync(
            string templateName,
            string bodyTemplate,
            string? subjectTemplate,
            Dictionary<string, string?> model,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class CapturingEmailSender : IEmailNotificationSender
    {
        public Task<NotificationProviderSendResult> SendAsync(
            string to,
            string subject,
            string htmlBody,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(NotificationProviderSendResult.Success("accepted"));
        }
    }

    private sealed class StubSmtpTransportFactory(ISmtpTransportClient transport) : ISmtpTransportFactory
    {
        public ISmtpTransportClient Create()
        {
            return transport;
        }
    }

    private sealed class AcceptedThenDisconnectFailsTransport : ISmtpTransportClient
    {
        public bool DisconnectAttempted { get; private set; }

        public Task ConnectAsync(
            string host,
            int port,
            SecureSocketOptions options,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task AuthenticateAsync(
            string userName,
            string password,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<string> SendAsync(MimeMessage message, CancellationToken cancellationToken = default)
        {
            return Task.FromResult("provider-accepted");
        }

        public Task DisconnectAsync(bool quit, CancellationToken cancellationToken = default)
        {
            DisconnectAttempted = true;
            throw new IOException("Connection closed after SMTP acceptance.");
        }

        public void Dispose()
        {
        }
    }
}
