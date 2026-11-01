using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Notifications.Application.Delivery;
using Xunit;
using SharedTokenProvider = Shared.Permissions.Features.IInternalAccessTokenProvider;

namespace Notifications.Tests;

/// <summary>
/// The delivery-time preference lookup calls Identity under the client profile it is configured with — Notifications'
/// own <c>NotificationsInternal</c> identity by default — never an implicit default profile the host does not have.
/// </summary>
public sealed class IdentityNotificationPreferenceClientTests
{
    private const string Token = "preference-token";

    [Fact]
    public async Task Lookup_requests_its_token_under_the_notifications_internal_profile_by_default()
    {
        var tokens = new RecordingTokenProvider(issueFor: "NotificationsInternal");
        var handler = new CapturingHandler("""{"enabled":false}""");
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var client = CreateClient(tokens, handler, new IdentityNotificationPreferenceClientOptions
        {
            BaseAddress = "http://identity.test/"
        });

        var enabled = await client.IsNotificationEnabledAsync(tenantId, userId, "new-device-sign-in");

        Assert.False(enabled);
        var requested = Assert.Single(tokens.Requests);
        Assert.Equal(("NotificationsInternal", "identity-internal"), requested);
        var request = Assert.Single(handler.Requests);
        Assert.Equal($"Bearer {Token}", request.Authorization);
        Assert.Equal(tenantId.ToString("D"), request.TenantHeader);
        Assert.Equal(
            $"/api/identity/internal/profile-preferences/users/{userId:D}/alerts/new-device-sign-in",
            request.Path);
    }

    [Fact]
    public async Task Lookup_uses_the_configured_client_profile_and_scope()
    {
        var tokens = new RecordingTokenProvider(issueFor: "PreferenceReader");
        var handler = new CapturingHandler("""{"enabled":true}""");
        var client = CreateClient(tokens, handler, new IdentityNotificationPreferenceClientOptions
        {
            BaseAddress = "http://identity.test/",
            ClientProfile = "PreferenceReader",
            Scope = "identity-preferences"
        });

        Assert.True(await client.IsNotificationEnabledAsync(Guid.NewGuid(), Guid.NewGuid(), "mfa-changed"));
        Assert.Equal(("PreferenceReader", "identity-preferences"), Assert.Single(tokens.Requests));
    }

    [Fact]
    public async Task Missing_token_fails_closed_without_calling_identity_anonymously()
    {
        // The host has no credential for the configured profile (the original defect: an 'Internal' profile lookup).
        var tokens = new RecordingTokenProvider(issueFor: "Internal");
        var handler = new CapturingHandler("""{"enabled":true}""");
        var client = CreateClient(tokens, handler, new IdentityNotificationPreferenceClientOptions
        {
            BaseAddress = "http://identity.test/"
        });

        Assert.False(await client.IsNotificationEnabledAsync(Guid.NewGuid(), Guid.NewGuid(), "risk-blocked"));
        Assert.Equal(("NotificationsInternal", "identity-internal"), Assert.Single(tokens.Requests));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void Preference_and_audience_clients_default_to_the_same_dedicated_identity()
    {
        Assert.Equal(
            new IdentityAudienceClientOptions().ClientProfile,
            new IdentityNotificationPreferenceClientOptions().ClientProfile);
        Assert.Equal(
            new IdentityAudienceClientOptions().Scope,
            new IdentityNotificationPreferenceClientOptions().Scope);
    }

    private static IdentityNotificationPreferenceClient CreateClient(
        RecordingTokenProvider tokens,
        CapturingHandler handler,
        IdentityNotificationPreferenceClientOptions options) =>
        new(
            new SingleClientFactory(handler, new Uri(options.BaseAddress!)),
            new InternalAccessTokenProvider(tokens),
            Options.Create(options),
            NullLogger<IdentityNotificationPreferenceClient>.Instance);

    private sealed class RecordingTokenProvider(string issueFor) : SharedTokenProvider
    {
        public List<(string Profile, string Scope)> Requests { get; } = [];

        public Task<string?> GetAccessTokenForClientAsync(
            string clientProfileName,
            string scope,
            CancellationToken cancellationToken = default)
        {
            Requests.Add((clientProfileName, scope));
            return Task.FromResult(string.Equals(clientProfileName, issueFor, StringComparison.Ordinal) ? Token : null);
        }
    }

    private sealed record CapturedRequest(string Path, string? Authorization, string? TenantHeader);

    private sealed class CapturingHandler(string body) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(
                request.RequestUri!.AbsolutePath,
                request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("X-Tenant-Id", out var values) ? values.Single() : null));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler, Uri baseAddress) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal(IdentityNotificationPreferenceClient.HttpClientName, name);
            return new HttpClient(handler, disposeHandler: false) { BaseAddress = baseAddress };
        }
    }
}
