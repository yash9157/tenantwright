using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Notifications.Application.Contracts.Channels;
using Notifications.Application.Contracts.Devices;
using Notifications.Application.Contracts.ProviderConfig;
using Notifications.Application.Contracts.ProviderConfig.Security;
using Notifications.Application.Delivery.Providers;
using Notifications.Application.Devices;
using Notifications.Application.Delivery;
using Notifications.Application.Contracts.Delivery;
using Notifications.Domain.Devices;
using Notifications.Domain.Shared.Enums;
using Xunit;

namespace Notifications.Tests;

public class WhatsAppPushProviderTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Live_whats_app_sender_fails_closed_until_governed_consent_exists()
    {
        var sender = new ConfigurableWhatsAppNotificationSender(
            Options.Create(new NotificationsChannelProviderModeOptions { WhatsApp = "Live" }));

        var exception = await Assert.ThrowsAsync<NotificationChannelUnavailableException>(() =>
            sender.SendAsync("+15551111111", "event_reminder", "Safe text"));

        Assert.Equal("whatsapp_consent_authority_unavailable", exception.ErrorCode);
    }

    // ---- WhatsApp adapter: approved-template enforcement ----

    [Fact]
    public async Task WhatsAppAdapter_sends_approved_template_and_returns_message_id()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{\"messages\":[{\"id\":\"wamid.ABC\"}]}");
        var adapter = new MetaWhatsAppCloudProviderAdapter(new StubHttpClientFactory(handler), NullLogger<MetaWhatsAppCloudProviderAdapter>.Instance);
        var config = BuildWhatsAppConfig(approvedTemplates: ["event_reminder"], accessToken: "token");

        var result = await adapter.SendAsync("+15551111111", "event_reminder", "Your event is tomorrow.", config);

        Assert.True(result.IsSuccess);
        Assert.Equal("wamid.ABC", result.ProviderMessageId);
    }

    [Fact]
    public async Task WhatsAppAdapter_rejects_unapproved_template_without_calling_provider()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{\"messages\":[{\"id\":\"wamid.ABC\"}]}");
        var adapter = new MetaWhatsAppCloudProviderAdapter(new StubHttpClientFactory(handler), NullLogger<MetaWhatsAppCloudProviderAdapter>.Instance);
        var config = BuildWhatsAppConfig(approvedTemplates: ["event_reminder"], accessToken: "token");

        var result = await adapter.SendAsync("+15551111111", "unapproved_marketing_blast", "Buy now!", config);

        Assert.False(result.IsSuccess);
        Assert.Equal("whatsapp_template_not_approved", result.ErrorCode);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task WhatsAppAdapter_maps_failure_to_error_code()
    {
        var handler = new StubHandler(HttpStatusCode.BadRequest, "{\"error\":{\"code\":131047,\"message\":\"Re-engagement message\"}}");
        var adapter = new MetaWhatsAppCloudProviderAdapter(new StubHttpClientFactory(handler), NullLogger<MetaWhatsAppCloudProviderAdapter>.Instance);
        var config = BuildWhatsAppConfig(approvedTemplates: ["event_reminder"], accessToken: "token");

        var result = await adapter.SendAsync("+15551111111", "event_reminder", "Your event is tomorrow.", config);

        Assert.False(result.IsSuccess);
        Assert.Equal("whatsapp_131047", result.ErrorCode);
    }

    // ---- Meta webhook signature validation ----

    [Fact]
    public void MetaWebhookSignature_valid_for_correctly_signed_body()
    {
        const string appSecret = "meta-app-secret";
        var body = Encoding.UTF8.GetBytes("{\"entry\":[]}");
        var signature = "sha256=" + Convert.ToHexString(ComputeHmacSha256(appSecret, body)).ToLowerInvariant();

        Assert.True(MetaWebhookSignatureValidator.IsValid(appSecret, body, signature));
    }

    [Fact]
    public void MetaWebhookSignature_invalid_when_tampered_or_wrong_secret()
    {
        const string appSecret = "meta-app-secret";
        var body = Encoding.UTF8.GetBytes("{\"entry\":[]}");
        var signature = "sha256=" + Convert.ToHexString(ComputeHmacSha256(appSecret, body)).ToLowerInvariant();

        var tamperedBody = Encoding.UTF8.GetBytes("{\"entry\":[{}]}");
        Assert.False(MetaWebhookSignatureValidator.IsValid(appSecret, tamperedBody, signature));
        Assert.False(MetaWebhookSignatureValidator.IsValid("wrong-secret", body, signature));
        Assert.False(MetaWebhookSignatureValidator.IsValid(appSecret, body, null));
        Assert.False(MetaWebhookSignatureValidator.IsValid(appSecret, body, "not-prefixed-correctly"));
    }

    // ---- FCM: JWT signing + OAuth2 exchange + send ----

    [Fact]
    public async Task FcmAccessTokenProvider_signs_a_verifiable_jwt_and_returns_the_exchanged_token()
    {
        using var rsa = RSA.Create(2048);
        var privateKeyPem = rsa.ExportPkcs8PrivateKeyPem();

        var capturingHandler = new CapturingTokenHandler("{\"access_token\":\"ya29.fake-token\",\"expires_in\":3599,\"token_type\":\"Bearer\"}");
        var provider = new FcmAccessTokenProvider(new StubHttpClientFactory(capturingHandler));

        var token = await provider.GetAccessTokenAsync("svc@project.iam.gserviceaccount.com", privateKeyPem, CancellationToken.None);

        Assert.Equal("ya29.fake-token", token);

        // Verify the JWT assertion the provider actually sent was correctly RS256-signed with the
        // matching public key, not just that *some* token was returned.
        var assertion = capturingHandler.CapturedAssertion!;
        var parts = assertion.Split('.');
        Assert.Equal(3, parts.Length);

        var signedData = Encoding.UTF8.GetBytes($"{parts[0]}.{parts[1]}");
        var signature = Base64UrlDecode(parts[2]);
        Assert.True(rsa.VerifyData(signedData, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

        var claimsJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
        using var claimsDoc = JsonDocument.Parse(claimsJson);
        Assert.Equal("svc@project.iam.gserviceaccount.com", claimsDoc.RootElement.GetProperty("iss").GetString());
        Assert.Equal("https://www.googleapis.com/auth/firebase.messaging", claimsDoc.RootElement.GetProperty("scope").GetString());
    }

    [Fact]
    public async Task FcmAdapter_sends_and_returns_provider_message_id()
    {
        using var rsa = RSA.Create(2048);
        var privateKeyPem = rsa.ExportPkcs8PrivateKeyPem();

        var tokenHandler = new CapturingTokenHandler("{\"access_token\":\"ya29.fake-token\"}");
        var sendHandler = new StubHandler(HttpStatusCode.OK, "{\"name\":\"projects/demo/messages/0:1234\"}");
        var routingHandler = new RoutingHandler(tokenHandler, sendHandler);

        var adapter = new FcmPushProviderAdapter(
            new StubHttpClientFactory(routingHandler),
            new FcmAccessTokenProvider(new StubHttpClientFactory(routingHandler)),
            NullLogger<FcmPushProviderAdapter>.Instance);

        var config = BuildFcmConfig(clientEmail: "svc@project.iam.gserviceaccount.com", privateKey: privateKeyPem);

        var result = await adapter.SendAsync("device-token-123", "Event reminder", "Tomorrow at 10am", config);

        Assert.True(result.IsSuccess);
        Assert.Equal("projects/demo/messages/0:1234", result.ProviderMessageId);
    }

    [Fact]
    public async Task FcmAdapter_maps_unregistered_token_failure_to_error_code()
    {
        using var rsa = RSA.Create(2048);
        var privateKeyPem = rsa.ExportPkcs8PrivateKeyPem();

        var tokenHandler = new CapturingTokenHandler("{\"access_token\":\"ya29.fake-token\"}");
        var sendHandler = new StubHandler(HttpStatusCode.NotFound, "{\"error\":{\"status\":\"UNREGISTERED\",\"message\":\"Requested entity was not found.\"}}");
        var routingHandler = new RoutingHandler(tokenHandler, sendHandler);

        var adapter = new FcmPushProviderAdapter(
            new StubHttpClientFactory(routingHandler),
            new FcmAccessTokenProvider(new StubHttpClientFactory(routingHandler)),
            NullLogger<FcmPushProviderAdapter>.Instance);

        var config = BuildFcmConfig(clientEmail: "svc@project.iam.gserviceaccount.com", privateKey: privateKeyPem);

        var result = await adapter.SendAsync("stale-device-token", "Title", "Body", config);

        Assert.False(result.IsSuccess);
        Assert.Equal("fcm_UNREGISTERED", result.ErrorCode);
    }

    [Fact]
    public async Task ApnsAdapter_sends_http2_bearer_request_to_the_explicit_sandbox_host()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var handler = new StubHandler(HttpStatusCode.OK, "{}");
        var adapter = new ApnsPushProviderAdapter(new StubHttpClientFactory(handler), NullLogger<ApnsPushProviderAdapter>.Instance);
        var config = new ResolvedNotificationProviderConfig
        {
            ConfigId = Guid.NewGuid(),
            ConfigTenantId = null,
            Channel = NotificationChannel.Push,
            ProviderType = "Apns",
            SettingsJson = JsonSerializer.Serialize(new ApnsSettings { TeamId = "TEAM", KeyId = "KEY", BundleId = "com.example.app", UseSandbox = true }, JsonOptions),
            Secret = JsonSerializer.Serialize(new ApnsSecrets { PrivateKey = key.ExportPkcs8PrivateKeyPem() }, JsonOptions)
        };

        var result = await adapter.SendAsync("device-token", "Title", "Body", config);

        Assert.True(result.IsSuccess);
        Assert.Equal("api.sandbox.push.apple.com", handler.LastRequestUri!.Host);
        Assert.StartsWith("bearer ", handler.LastAuthorization!, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Device registration ----

    [Fact]
    public async Task DeviceRegistration_register_then_list_then_unregister_roundtrip()
    {
        var tenantId = Guid.NewGuid();
        using var db = NotificationsEfCoreTestHarness.BuildDbContext(
            Guid.NewGuid().ToString(), tenantId: tenantId, multiTenantFilterEnabled: false);

        var service = new DeviceRegistrationAppService(
            new EfTestRepository<NotificationDeviceToken, Guid>(db),
            new TestCurrentTenant(tenantId),
            new TestCurrentPrincipalAccessor("user-1"),
            new TestDeviceTokenProtector());

        var registered = await service.RegisterAsync(new RegisterDeviceTokenDto
        {
            DeviceToken = "device-abc",
            Platform = NotificationDevicePlatform.Android,
            AppIdentifier = "com.example.app"
        });

        Assert.True(registered.IsActive);
        Assert.StartsWith("…", registered.DeviceTokenMask, StringComparison.Ordinal);

        var myDevices = await service.GetMyDevicesAsync();
        Assert.Single(myDevices);

        await service.UnregisterAsync(registered.Id);

        var afterUnregister = await service.GetMyDevicesAsync();
        Assert.Empty(afterUnregister);
    }

    [Fact]
    public async Task DeviceRegistration_registering_the_same_token_again_reactivates_instead_of_duplicating()
    {
        var tenantId = Guid.NewGuid();
        using var db = NotificationsEfCoreTestHarness.BuildDbContext(
            Guid.NewGuid().ToString(), tenantId: tenantId, multiTenantFilterEnabled: false);

        var service = new DeviceRegistrationAppService(
            new EfTestRepository<NotificationDeviceToken, Guid>(db),
            new TestCurrentTenant(tenantId),
            new TestCurrentPrincipalAccessor("user-1"),
            new TestDeviceTokenProtector());

        var rawToken = new string('x', 512);
        var first = await service.RegisterAsync(new RegisterDeviceTokenDto { DeviceToken = rawToken, Platform = NotificationDevicePlatform.Ios });
        await service.UnregisterAsync(first.Id);
        var second = await service.RegisterAsync(new RegisterDeviceTokenDto { DeviceToken = rawToken, Platform = NotificationDevicePlatform.Ios });

        Assert.Equal(first.Id, second.Id);
        Assert.True(second.IsActive);

        var all = await db.NotificationDeviceTokens.IgnoreQueryFilters().Where(x => x.DeviceTokenHash != null).ToListAsync();
        Assert.Single(all);
        Assert.True(all[0].DeviceToken.Length > 512);
        Assert.NotEqual(rawToken, all[0].DeviceToken);
    }

    [Fact]
    public async Task DeviceRegistration_stores_web_push_subscription_as_protected_payload_and_masks_it()
    {
        var tenantId = Guid.NewGuid();
        using var db = NotificationsEfCoreTestHarness.BuildDbContext(Guid.NewGuid().ToString(), tenantId: tenantId, multiTenantFilterEnabled: false);
        var service = new DeviceRegistrationAppService(new EfTestRepository<NotificationDeviceToken, Guid>(db), new TestCurrentTenant(tenantId), new TestCurrentPrincipalAccessor("user-1"), new TestDeviceTokenProtector());

        var registered = await service.RegisterAsync(new RegisterDeviceTokenDto
        {
            Platform = NotificationDevicePlatform.Web,
            WebPushEndpoint = "https://push.example.test/subscription/one",
            WebPushP256dh = "p256dh-key",
            WebPushAuth = "auth-key"
        });

        Assert.True(registered.IsActive);
        Assert.Equal(NotificationDevicePlatform.Web, registered.Platform);
        var stored = await db.NotificationDeviceTokens.SingleAsync();
        Assert.DoesNotContain("push.example.test", stored.DeviceToken, StringComparison.Ordinal);
        Assert.NotNull(stored.DeviceTokenHash);
    }

    private static ResolvedNotificationProviderConfig BuildWhatsAppConfig(List<string> approvedTemplates, string accessToken)
    {
        var settingsJson = JsonSerializer.Serialize(
            new MetaWhatsAppCloudSettings { PhoneNumberId = "123456", ApprovedTemplateNames = approvedTemplates },
            JsonOptions);
        var secretJson = JsonSerializer.Serialize(new MetaWhatsAppCloudSecrets { AccessToken = accessToken }, JsonOptions);

        return new ResolvedNotificationProviderConfig
        {
            ConfigId = Guid.NewGuid(),
            ConfigTenantId = null,
            Channel = NotificationChannel.WhatsApp,
            ProviderType = "MetaWhatsAppCloud",
            SettingsJson = settingsJson,
            Secret = secretJson
        };
    }

    private static ResolvedNotificationProviderConfig BuildFcmConfig(string clientEmail, string privateKey)
    {
        var settingsJson = JsonSerializer.Serialize(new FcmSettings { ProjectId = "demo" }, JsonOptions);
        var secretJson = JsonSerializer.Serialize(new FcmSecrets { ClientEmail = clientEmail, PrivateKey = privateKey }, JsonOptions);

        return new ResolvedNotificationProviderConfig
        {
            ConfigId = Guid.NewGuid(),
            ConfigTenantId = null,
            Channel = NotificationChannel.Push,
            ProviderType = "Fcm",
            SettingsJson = settingsJson,
            Secret = secretJson
        };
    }

    private static byte[] ComputeHmacSha256(string key, byte[] data)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        return hmac.ComputeHash(data);
    }

    private sealed class TestDeviceTokenProtector : INotificationsProviderSecretProtector
    {
        public string Protect(Guid? tenantId, NotificationChannel channel, Guid configId, string plaintext) => $"protected:{Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext))}";
        public string Unprotect(Guid? tenantId, NotificationChannel channel, Guid configId, string storedValue) => Encoding.UTF8.GetString(Convert.FromBase64String(storedValue["protected:".Length..]));
        public bool IsProtected(string? storedValue) => storedValue?.StartsWith("protected:", StringComparison.Ordinal) == true;
        public bool IsProtectedByCurrentKey(string? storedValue) => IsProtected(storedValue);
        public string Fingerprint(string plaintext) => "test";
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }

        return Convert.FromBase64String(padded);
    }

    private static string? ParseFormValue(string formUrlEncoded, string key)
    {
        foreach (var pair in formUrlEncoded.Split('&'))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && Uri.UnescapeDataString(parts[0]) == key)
            {
                return Uri.UnescapeDataString(parts[1]);
            }
        }

        return null;
    }

    private interface IStubResponder
    {
        Task<HttpResponseMessage> RespondAsync(HttpRequestMessage request, CancellationToken cancellationToken);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private sealed class StubHandler : HttpMessageHandler, IStubResponder
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _body;

        public int CallCount { get; private set; }
        public Uri? LastRequestUri { get; private set; }
        public string? LastAuthorization { get; private set; }

        public StubHandler(HttpStatusCode statusCode, string body)
        {
            _statusCode = statusCode;
            _body = body;
        }

        public Task<HttpResponseMessage> RespondAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequestUri = request.RequestUri;
            LastAuthorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            });
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => RespondAsync(request, cancellationToken);
    }

    private sealed class CapturingTokenHandler : HttpMessageHandler, IStubResponder
    {
        private readonly string _responseBody;

        public string? CapturedAssertion { get; private set; }

        public CapturingTokenHandler(string responseBody) => _responseBody = responseBody;

        public async Task<HttpResponseMessage> RespondAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content != null)
            {
                var form = await request.Content.ReadAsStringAsync(cancellationToken);
                CapturedAssertion = ParseFormValue(form, "assertion");
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseBody, Encoding.UTF8, "application/json")
            };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => RespondAsync(request, cancellationToken);
    }

    /// <summary>Routes Google's OAuth2 token-exchange calls to one handler and the FCM send call to another,
    /// since <see cref="FcmPushProviderAdapter"/> and <see cref="FcmAccessTokenProvider"/> share one named
    /// HttpClient in production but hit two different hosts.</summary>
    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly IStubResponder _tokenHandler;
        private readonly IStubResponder _sendHandler;

        public RoutingHandler(IStubResponder tokenHandler, IStubResponder sendHandler)
        {
            _tokenHandler = tokenHandler;
            _sendHandler = sendHandler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var target = request.RequestUri?.Host.Contains("oauth2", StringComparison.OrdinalIgnoreCase) == true
                ? _tokenHandler
                : _sendHandler;

            return target.RespondAsync(request, cancellationToken);
        }
    }
}
