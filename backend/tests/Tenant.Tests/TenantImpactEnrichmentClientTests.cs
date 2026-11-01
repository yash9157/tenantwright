using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shared.Permissions.Features;
using Tenant.Application.Tenants;
using Xunit;

namespace Tenant.Tests;

/// <summary>Impact enrichment requests its M2M tokens under the client profile it is configured with.</summary>
public sealed class TenantImpactEnrichmentClientTests
{
    [Fact]
    public async Task Counts_are_read_under_the_configured_client_profile()
    {
        var tokens = new RecordingTokenProvider(issueFor: "TenantImpactReader");
        var client = new TenantImpactEnrichmentClient(
            new CountingClientFactory(),
            tokens,
            Options.Create(new TenantImpactEnrichmentOptions { ClientProfile = "TenantImpactReader" }),
            NullLogger<TenantImpactEnrichmentClient>.Instance);

        var result = await client.GetAsync(Guid.NewGuid());

        Assert.True(result.Identity.IsAvailable);
        Assert.Equal(3, result.Identity.Count);
        Assert.True(result.Notifications.IsAvailable);
        Assert.Equal(
            [("TenantImpactReader", "identity-internal"), ("TenantImpactReader", "notifications-internal")],
            tokens.Requests.Order());
    }

    [Fact]
    public async Task A_profile_without_credentials_reports_the_counts_unavailable_without_calling_anyone()
    {
        var tokens = new RecordingTokenProvider(issueFor: "SomeOtherProfile");
        var factory = new CountingClientFactory();
        var client = new TenantImpactEnrichmentClient(
            factory,
            tokens,
            Options.Create(new TenantImpactEnrichmentOptions()),
            NullLogger<TenantImpactEnrichmentClient>.Instance);

        var result = await client.GetAsync(Guid.NewGuid());

        Assert.False(result.Identity.IsAvailable);
        Assert.False(result.Notifications.IsAvailable);
        Assert.All(tokens.Requests, request => Assert.Equal("Internal", request.Profile));
        Assert.Equal(0, factory.Calls);
    }

    private sealed class RecordingTokenProvider(string issueFor) : IInternalAccessTokenProvider
    {
        private readonly object _gate = new();

        public List<(string Profile, string Scope)> Requests { get; } = [];

        public Task<string?> GetAccessTokenForClientAsync(
            string clientProfileName,
            string scope,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                Requests.Add((clientProfileName, scope));
            }

            return Task.FromResult(string.Equals(clientProfileName, issueFor, StringComparison.Ordinal) ? "token" : null);
        }
    }

    private sealed class CountingClientFactory : IHttpClientFactory
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public HttpClient CreateClient(string name)
        {
            Interlocked.Increment(ref _calls);
            return new HttpClient(new CountHandler()) { BaseAddress = new Uri("http://dependency.test/") };
        }
    }

    private sealed class CountHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"activeSessionCount":3,"pendingDeliveryCount":4}""",
                    Encoding.UTF8,
                    "application/json")
            });
    }
}
