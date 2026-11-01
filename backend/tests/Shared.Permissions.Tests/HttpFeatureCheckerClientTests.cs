using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shared.Caching.Abstractions;
using Shared.Permissions.Features;
using Xunit;

namespace Shared.Permissions.Tests;

public class HttpFeatureCheckerClientTests
{
    [Fact]
    public async Task AreEnabledAsync_AddsBearerToken_WhenTokenProviderReturnsToken()
    {
        var handler = new CaptureHandler(_ =>
        {
            var payload = "{\"results\":{\"Feature.A\":true}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            });
        });

        var client = CreateClient(
            handler,
            new FeatureCheckerClientOptions
            {
                Enabled = true,
                FailOpen = false,
                BaseAddress = "https://localhost:7163",
                TimeoutSeconds = 2
            },
            new StaticTokenProvider("token-123"));

        var result = await client.AreEnabledAsync(null, new[] { "Feature.A" }, CancellationToken.None);

        Assert.True(result["Feature.A"]);
        Assert.NotNull(handler.LastAuthorization);
        Assert.Equal("Bearer", handler.LastAuthorization!.Scheme);
        Assert.Equal("token-123", handler.LastAuthorization.Parameter);
    }

    [Fact]
    public async Task AreEnabledAsync_FailClosed_OnUnauthorized_WhenTokenMissing()
    {
        var handler = new CaptureHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var client = CreateClient(
            handler,
            new FeatureCheckerClientOptions
            {
                Enabled = true,
                FailOpen = false,
                BaseAddress = "https://localhost:7163",
                TimeoutSeconds = 2
            },
            new StaticTokenProvider(null));

        var result = await client.AreEnabledAsync(null, new[] { "Feature.B" }, CancellationToken.None);

        Assert.False(result["Feature.B"]);
        Assert.Null(handler.LastAuthorization);
    }

    private static HttpFeatureCheckerClient CreateClient(
        HttpMessageHandler handler,
        FeatureCheckerClientOptions options,
        IRemoteFeatureAccessTokenProvider tokenProvider)
    {
        var httpClient = new HttpClient(handler);
        var factory = new StaticHttpClientFactory(httpClient);
        var cache = new InMemoryHybridCache();

        return new HttpFeatureCheckerClient(
            factory,
            Options.Create(options),
            cache,
            NullLogger<HttpFeatureCheckerClient>.Instance,
            tokenProvider);
    }

    private sealed class StaticHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _httpClient;

        public StaticHttpClientFactory(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public HttpClient CreateClient(string name)
        {
            return _httpClient;
        }
    }

    private sealed class StaticTokenProvider : IRemoteFeatureAccessTokenProvider
    {
        private readonly string? _token;

        public StaticTokenProvider(string? token)
        {
            _token = token;
        }

        public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_token);
        }
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public CaptureHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        public AuthenticationHeaderValue? LastAuthorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastAuthorization = request.Headers.Authorization;
            return await _handler(request);
        }
    }

    private sealed class InMemoryHybridCache : IHybridCache<HttpFeatureCheckerClient.FeatureValueCacheItem, string>
    {
        private readonly Dictionary<string, HttpFeatureCheckerClient.FeatureValueCacheItem> _items = new(StringComparer.Ordinal);

        public async Task<HttpFeatureCheckerClient.FeatureValueCacheItem?> GetOrCreateAsync(
            string key,
            Func<Task<HttpFeatureCheckerClient.FeatureValueCacheItem>> factory,
            Func<HybridCacheEntryOptions>? optionsFactory = null,
            bool? hideErrors = null,
            bool considerUow = false,
            CancellationToken token = default)
        {
            if (_items.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var created = await factory();
            _items[key] = created;
            return created;
        }

        public Task SetAsync(
            string key,
            HttpFeatureCheckerClient.FeatureValueCacheItem value,
            HybridCacheEntryOptions? options = null,
            bool? hideErrors = null,
            bool considerUow = false,
            CancellationToken token = default)
        {
            _items[key] = value;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(
            string key,
            bool? hideErrors = null,
            bool considerUow = false,
            CancellationToken token = default)
        {
            _items.Remove(key);
            return Task.CompletedTask;
        }

        public Task RemoveManyAsync(
            IEnumerable<string> keys,
            bool? hideErrors = null,
            bool considerUow = false,
            CancellationToken token = default)
        {
            foreach (var key in keys)
            {
                _items.Remove(key);
            }

            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task AreEnabledAsync_honors_fail_open_when_the_remote_payload_is_null()
    {
        var handler = new CaptureHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("null", Encoding.UTF8, "application/json")
        }));
        var client = CreateClient(
            handler,
            new FeatureCheckerClientOptions
            {
                Enabled = true,
                FailOpen = true,
                BaseAddress = "https://localhost:7163",
                TimeoutSeconds = 2
            },
            new StaticTokenProvider(null));

        var result = await client.AreEnabledAsync(null, new[] { "Feature.NullPayload" }, CancellationToken.None);

        Assert.True(result["Feature.NullPayload"]);
    }
}
