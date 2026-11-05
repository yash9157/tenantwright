using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Notifications.Application.Contracts.Channels;
using Notifications.Application.Contracts.Delivery;
using Notifications.Application.Contracts.ProviderConfig;
using Notifications.Application.Delivery;
using Notifications.Application.Delivery.Providers;
using Notifications.Domain.Notifications;
using Notifications.Domain.Shared.Enums;
using Shared.MultiTenancy;
using Xunit;

namespace Notifications.Tests;

public class TwilioSmsProviderTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // ---- Adapter ----

    [Fact]
    public async Task Adapter_maps_success_to_provider_message_id()
    {
        var handler = new StubHandler(HttpStatusCode.Created, "{\"sid\":\"SM123\",\"status\":\"queued\"}");
        var adapter = BuildAdapter(handler);
        var config = BuildResolvedConfig(accountSid: "AC1", authToken: "token", fromNumber: "+15550000000");

        var result = await adapter.SendAsync("+15551111111", "hello", config);

        Assert.True(result.IsSuccess);
        Assert.Equal("SM123", result.ProviderMessageId);
    }

    [Theory]
    [InlineData("{\"code\":21211,\"message\":\"Invalid 'To'\"}", "twilio_21211")]
    [InlineData("{\"code\":{\"recipient\":\"private-recipient\"}}", "twilio_400")]
    [InlineData("{\"code\":\"private-recipient\"}", "twilio_400")]
    public async Task Adapter_exposes_only_numeric_provider_error_codes(string body, string expectedCode)
    {
        var handler = new StubHandler(HttpStatusCode.BadRequest, body);
        var adapter = BuildAdapter(handler);
        var config = BuildResolvedConfig(accountSid: "AC1", authToken: "token", fromNumber: "+15550000000");

        var result = await adapter.SendAsync("+bad", "hello", config);

        Assert.False(result.IsSuccess);
        Assert.Equal(expectedCode, result.ErrorCode);
    }

    [Fact]
    public async Task Adapter_throws_when_not_configured()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{}");
        var adapter = BuildAdapter(handler);
        var config = BuildResolvedConfig(accountSid: "", authToken: null, fromNumber: "");

        await Assert.ThrowsAsync<NotificationChannelUnavailableException>(() => adapter.SendAsync("+1", "x", config));
    }

    // ---- Sender mode routing ----

    [Fact]
    public async Task Sender_disabled_throws()
    {
        var sender = new ConfigurableSmsNotificationSender(
            Options.Create(new NotificationsChannelProviderModeOptions { Sms = "Disabled" }),
            new TestCurrentTenant(null),
            new NoopResolver(),
            []);

        await Assert.ThrowsAsync<NotificationChannelUnavailableException>(() => sender.SendAsync("+1", "x"));
    }

    [Fact]
    public async Task Sender_mock_returns_mock_id_without_adapter()
    {
        var sender = new ConfigurableSmsNotificationSender(
            Options.Create(new NotificationsChannelProviderModeOptions { Sms = "MockSuccess" }),
            new TestCurrentTenant(null),
            new NoopResolver(),
            []);

        var outcome = await sender.SendAsync("+1", "x");
        Assert.StartsWith("mock-sms-", outcome.ProviderMessageId, StringComparison.Ordinal);
        Assert.Null(outcome.ProviderConfigId);
    }

    [Fact]
    public async Task Sender_live_delegates_to_matching_adapter()
    {
        var configId = Guid.NewGuid();
        var adapter = new FakeSmsAdapter(NotificationProviderSendResult.Success("SM999"));
        var sender = new ConfigurableSmsNotificationSender(
            Options.Create(new NotificationsChannelProviderModeOptions { Sms = "Live" }),
            new TestCurrentTenant(null),
            new StaticResolver(BuildResolvedConfig(accountSid: "AC1", authToken: "token", fromNumber: "+1", configId: configId)),
            [adapter]);

        var outcome = await sender.SendAsync("+15551111111", "hello");

        Assert.Equal("SM999", outcome.ProviderMessageId);
        Assert.Equal(configId, outcome.ProviderConfigId);
        Assert.Equal(1, adapter.CallCount);
    }

    [Fact]
    public async Task Sender_live_preserves_the_typed_adapter_rejection()
    {
        var adapter = new FakeSmsAdapter(NotificationProviderSendResult.PermanentRejection("twilio_21610"));
        var sender = new ConfigurableSmsNotificationSender(
            Options.Create(new NotificationsChannelProviderModeOptions { Sms = "Live" }),
            new TestCurrentTenant(null),
            new StaticResolver(BuildResolvedConfig(accountSid: "AC1", authToken: "token", fromNumber: "+1")),
            [adapter]);

        var result = await sender.SendAsync("+1", "x");
        Assert.Equal(NotificationSendOutcome.PermanentRejection, result.Outcome);
        Assert.Equal("twilio_21610", result.ErrorCode);
    }

    [Fact]
    public async Task Sender_live_throws_when_no_configuration_resolves()
    {
        var sender = new ConfigurableSmsNotificationSender(
            Options.Create(new NotificationsChannelProviderModeOptions { Sms = "Live" }),
            new TestCurrentTenant(null),
            new NoopResolver(),
            []);

        await Assert.ThrowsAsync<NotificationChannelUnavailableException>(() => sender.SendAsync("+1", "x"));
    }

    // ---- Signature validation ----

    [Fact]
    public void Signature_valid_for_correctly_signed_request()
    {
        const string token = "my-auth-token";
        const string url = "https://tenantwright.example/api/notifications/webhooks/twilio/sms";
        var form = new[]
        {
            new KeyValuePair<string, string>("MessageSid", "SM1"),
            new KeyValuePair<string, string>("MessageStatus", "delivered")
        };

        var signature = ComputeTwilioSignature(token, url, form);

        Assert.True(TwilioSignatureValidator.IsValid(token, url, form, signature));
    }

    [Fact]
    public void Signature_invalid_when_tampered_or_missing()
    {
        const string token = "my-auth-token";
        const string url = "https://tenantwright.example/api/notifications/webhooks/twilio/sms";
        var form = new[] { new KeyValuePair<string, string>("MessageSid", "SM1") };
        var signature = ComputeTwilioSignature(token, url, form);

        // Tampered body
        var tampered = new[] { new KeyValuePair<string, string>("MessageSid", "SM2") };
        Assert.False(TwilioSignatureValidator.IsValid(token, url, tampered, signature));

        // Wrong token
        Assert.False(TwilioSignatureValidator.IsValid("other-token", url, form, signature));

        // Missing header
        Assert.False(TwilioSignatureValidator.IsValid(token, url, form, null));
    }

    // ---- Webhook receipt (idempotent, host-context) ----

    [Theory]
    [InlineData("30008", "provider_reported_failure:30008")]
    [InlineData("private-recipient", "provider_reported_failure")]
    public async Task Receipt_failure_dead_letters_sent_delivery_and_is_idempotent(string providerCode, string expectedReason)
    {
        var tenant = Guid.NewGuid();
        using var db = NotificationsEfCoreTestHarness.BuildDbContext(
            Guid.NewGuid().ToString(), tenantId: tenant, multiTenantFilterEnabled: false);
        var notificationId = Guid.NewGuid();
        db.Notifications.Add(new Notification(notificationId, tenant, null, NotificationChannel.Sms, "sms-receipt", "{}"));
        var delivery = new NotificationDelivery(1, tenant, notificationId, recipientId: 1);
        var configId = Guid.NewGuid();
        delivery.MarkProviderAccepted(DateTime.UtcNow, "SM-abc", configId);
        db.NotificationDeliveries.Add(delivery);
        await db.SaveChangesAsync();

        var service = new NotificationDeliveryReceiptService(NotificationsEfCoreTestHarness.AsDbContextProvider(db));

        Assert.True(await service.ApplyProviderStatusAsync("SM-abc", NotificationChannel.Sms, configId, NotificationDeliveryReceiptStatus.Failed, providerCode));
        var afterFirst = await db.NotificationDeliveries.IgnoreQueryFilters().FirstAsync(x => x.Id == 1);
        Assert.Equal(NotificationStatus.DeadLettered, afterFirst.Status);
        Assert.Equal(expectedReason, afterFirst.LastError);

        // Idempotent: re-applying keeps it dead-lettered.
        Assert.True(await service.ApplyProviderStatusAsync("SM-abc", NotificationChannel.Sms, configId, NotificationDeliveryReceiptStatus.Failed, providerCode));
        var afterSecond = await db.NotificationDeliveries.IgnoreQueryFilters().FirstAsync(x => x.Id == 1);
        Assert.Equal(NotificationStatus.DeadLettered, afterSecond.Status);
    }

    [Fact]
    public async Task Receipt_delivered_confirms_final_delivery_and_unknown_sid_returns_false()
    {
        var tenant = Guid.NewGuid();
        using var db = NotificationsEfCoreTestHarness.BuildDbContext(
            Guid.NewGuid().ToString(), tenantId: tenant, multiTenantFilterEnabled: false);
        var notificationId = Guid.NewGuid();
        db.Notifications.Add(new Notification(notificationId, tenant, null, NotificationChannel.Sms, "sms-receipt", "{}"));
        var delivery = new NotificationDelivery(1, tenant, notificationId, recipientId: 1);
        var configId = Guid.NewGuid();
        delivery.MarkProviderAccepted(DateTime.UtcNow, "SM-ok", configId);
        db.NotificationDeliveries.Add(delivery);
        await db.SaveChangesAsync();

        var service = new NotificationDeliveryReceiptService(NotificationsEfCoreTestHarness.AsDbContextProvider(db));

        Assert.True(await service.ApplyProviderStatusAsync("SM-ok", NotificationChannel.Sms, configId, NotificationDeliveryReceiptStatus.Delivered, null));
        var reloaded = await db.NotificationDeliveries.IgnoreQueryFilters().FirstAsync(x => x.Id == 1);
        Assert.Equal(NotificationStatus.Delivered, reloaded.Status);

        Assert.False(await service.ApplyProviderStatusAsync("does-not-exist", NotificationChannel.Sms, configId, NotificationDeliveryReceiptStatus.Failed, null));
    }

    private static TwilioSmsProviderAdapter BuildAdapter(HttpMessageHandler handler)
    {
        return new TwilioSmsProviderAdapter(
            new StubHttpClientFactory(handler),
            NullLogger<TwilioSmsProviderAdapter>.Instance);
    }

    private static ResolvedNotificationProviderConfig BuildResolvedConfig(
        string accountSid,
        string? authToken,
        string fromNumber,
        Guid? configId = null)
    {
        var settingsJson = JsonSerializer.Serialize(
            new TwilioSmsSettings { AccountSid = accountSid, FromNumber = fromNumber },
            JsonOptions);

        return new ResolvedNotificationProviderConfig
        {
            ConfigId = configId ?? Guid.NewGuid(),
            ConfigTenantId = null,
            Channel = NotificationChannel.Sms,
            ProviderType = "Twilio",
            SettingsJson = settingsJson,
            Secret = authToken
        };
    }

    private static string ComputeTwilioSignature(string token, string url, IEnumerable<KeyValuePair<string, string>> form)
    {
        var builder = new StringBuilder(url);
        foreach (var pair in form.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            builder.Append(pair.Key).Append(pair.Value);
        }

#pragma warning disable CA5350 // Test parity with Twilio's mandated HMAC-SHA1 request-signature protocol.
        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(token));
#pragma warning restore CA5350
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private sealed class FakeSmsAdapter : ISmsProviderAdapter
    {
        private readonly NotificationProviderSendResult _result;

        public FakeSmsAdapter(NotificationProviderSendResult result, string providerName = "Twilio")
        {
            _result = result;
            ProviderName = providerName;
        }

        public string ProviderName { get; }
        public int CallCount { get; private set; }

        public Task<NotificationProviderSendResult> SendAsync(
            string toPhoneNumber,
            string message,
            ResolvedNotificationProviderConfig config,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(_result);
        }
    }

    private sealed class StaticResolver : INotificationProviderConfigResolver
    {
        private readonly ResolvedNotificationProviderConfig? _result;

        public StaticResolver(ResolvedNotificationProviderConfig? result) => _result = result;

        public Task<ResolvedNotificationProviderConfig?> ResolveAsync(Guid? tenantId, NotificationChannel channel, CancellationToken cancellationToken = default)
            => Task.FromResult(_result);

        public Task<ResolvedNotificationProviderConfig?> ResolveByProviderMessageIdAsync(string providerMessageId, NotificationChannel channel, CancellationToken cancellationToken = default)
            => Task.FromResult(_result);

        public Task<ResolvedNotificationProviderConfig?> ResolveByConfigIdAsync(Guid configId, NotificationChannel channel, CancellationToken cancellationToken = default)
            => Task.FromResult(_result);
    }

    private sealed class NoopResolver : INotificationProviderConfigResolver
    {
        public Task<ResolvedNotificationProviderConfig?> ResolveAsync(Guid? tenantId, NotificationChannel channel, CancellationToken cancellationToken = default)
            => Task.FromResult<ResolvedNotificationProviderConfig?>(null);

        public Task<ResolvedNotificationProviderConfig?> ResolveByProviderMessageIdAsync(string providerMessageId, NotificationChannel channel, CancellationToken cancellationToken = default)
            => Task.FromResult<ResolvedNotificationProviderConfig?>(null);

        public Task<ResolvedNotificationProviderConfig?> ResolveByConfigIdAsync(Guid configId, NotificationChannel channel, CancellationToken cancellationToken = default)
            => Task.FromResult<ResolvedNotificationProviderConfig?>(null);
    }

    private sealed class TestCurrentTenant : ICurrentTenant
    {
        public TestCurrentTenant(Guid? id) => Id = id;
        public bool IsAvailable => Id.HasValue;
        public Guid? Id { get; }
        public string? Name => null;
        public IDisposable Change(Guid? id, string? name = null) => new NoopScope();
        private sealed class NoopScope : IDisposable { public void Dispose() { } }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _body;

        public StubHandler(HttpStatusCode statusCode, string body)
        {
            _statusCode = statusCode;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            });
        }
    }
}
