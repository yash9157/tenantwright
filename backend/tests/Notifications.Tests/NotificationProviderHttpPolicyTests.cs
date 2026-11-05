using System.Diagnostics;
using System.Net;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Notifications.Application.Delivery.Providers;
using Notifications.Domain.Shared.Enums;
using Xunit;

namespace Notifications.Tests;

public sealed class NotificationProviderHttpPolicyTests
{
    [Theory]
    [InlineData(TwilioSmsProviderAdapter.HttpClientName)]
    [InlineData(MetaWhatsAppCloudProviderAdapter.HttpClientName)]
    [InlineData(FcmPushProviderAdapter.HttpClientName)]
    [InlineData(ApnsPushProviderAdapter.HttpClientName)]
    [InlineData(WebPushProviderAdapter.HttpClientName)]
    public async Task Send_posts_are_never_automatically_retried_or_logged_with_the_private_url(string clientName)
    {
        var services = new ServiceCollection();
        var logs = new CapturingProviderLogs();
        services.AddLogging(logging => logging.AddProvider(logs));
        services.ConfigureHttpClientDefaults(client => client.AddStandardResilienceHandler());
        NotificationsHostModule.ConfigureProviderHttpClients(services);
        var transport = new CountingTransport();
        services.AddHttpClient(clientName).ConfigurePrimaryHttpMessageHandler(() => transport);
        await using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(clientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://provider.invalid/private-subscription-capability");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, transport.CallCount);
        Assert.DoesNotContain(logs.Messages, message => message.Contains("private-subscription-capability", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Attempt_timeout_does_not_repeat_an_unsafe_send()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.ConfigureHttpClientDefaults(client => client.AddStandardResilienceHandler());
        NotificationsHostModule.ConfigureProviderHttpClients(services);
        services.PostConfigureAll<HttpStandardResilienceOptions>(options =>
        {
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(1);
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(5);
        });
        var transport = new CountingTransport(waitForCancellation: true);
        services.AddHttpClient(TwilioSmsProviderAdapter.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => transport);
        await using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(TwilioSmsProviderAdapter.HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://provider.invalid/private-subscription-capability");
        var exception = await Record.ExceptionAsync(() => client.SendAsync(request));
        Assert.Equal("Polly.Timeout.TimeoutRejectedException", exception?.GetType().FullName);
        Assert.Equal(1, transport.CallCount);
    }

    [Fact]
    public void Private_provider_scope_suppresses_child_http_spans_even_without_a_sampled_provider_span()
    {
        var filter = typeof(Microsoft.Extensions.Hosting.Extensions).GetMethod("ShouldInstrumentHttpRequest", BindingFlags.NonPublic | BindingFlags.Static)!;
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://provider.invalid/private-subscription-capability");
        Assert.True((bool)filter.Invoke(null, [request])!);
        using (var provider = NotificationProviderTrace.Start(NotificationChannel.Push))
        {
            using var child = new Activity("System.Net.Http.HttpRequestOut").Start();
            Assert.False((bool)filter.Invoke(null, [request])!);
            Assert.Equal("push", provider.GetTagItem("notification.channel"));
            Assert.DoesNotContain(provider.Tags, tag => tag.Value?.Contains("private-subscription-capability", StringComparison.Ordinal) == true);
        }
        Assert.True((bool)filter.Invoke(null, [request])!);
    }

    private sealed class CountingTransport(bool waitForCancellation = false) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            if (waitForCancellation)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }
    }

    private sealed class CapturingProviderLogs : ILoggerProvider
    {
        public List<string> Messages { get; } = [];
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Messages);
        public void Dispose() { }
        private sealed class CapturingLogger(List<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                messages.Add(formatter(state, exception));
        }
    }
}
