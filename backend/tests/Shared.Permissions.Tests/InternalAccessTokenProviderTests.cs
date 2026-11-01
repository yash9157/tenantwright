using System.Net;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Shared.Caching.Abstractions;
using Shared.DistributedLocking.Abstractions;
using Shared.DistributedLocking.Options;
using Shared.Permissions.Features;
using Xunit;

namespace Shared.Permissions.Tests;

public sealed class InternalAccessTokenProviderTests
{
    private const string Authority = "https://identity.test";
    private const string ClientSecret = "test-secret-must-not-be-logged";
    private const string Token = "test-token-must-not-be-logged";

    [Fact]
    public async Task Tenant_bound_tokens_partition_cache_and_locks_by_tenant_client_and_scope()
    {
        var forms = new List<string>();
        var cache = new RecordingCache();
        var distributedLock = new RecordingDistributedLock();
        var sut = CreateProvider(
            new RecordingHttpClientFactory(async (request, cancellationToken) =>
            {
                forms.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
                return TokenResponse();
            }),
            cache,
            distributedLock);
        var firstTenant = Guid.CreateVersion7();
        var secondTenant = Guid.CreateVersion7();

        Assert.Equal(Token, await sut.GetAccessTokenForClientAsync("Notifications", "notifications.read", firstTenant));
        Assert.Equal(Token, await sut.GetAccessTokenForClientAsync("Notifications", "notifications.read", firstTenant));
        Assert.Equal(Token, await sut.GetAccessTokenForClientAsync("Notifications", "notifications.read", secondTenant));
        Assert.Equal(Token, await sut.GetAccessTokenForClientAsync("Notifications", "notifications.write", firstTenant));
        Assert.Equal(Token, await sut.GetAccessTokenForClientAsync("AuditLogs", "notifications.read", firstTenant));

        Assert.Equal(4, forms.Count);
        Assert.All(forms, form => Assert.Contains("tenant_id=", form, StringComparison.Ordinal));
        Assert.Contains(forms, form => form.Contains(Uri.EscapeDataString(firstTenant.ToString("D")), StringComparison.Ordinal));
        Assert.Contains(forms, form => form.Contains(Uri.EscapeDataString(secondTenant.ToString("D")), StringComparison.Ordinal));

        Assert.Equal(4, distributedLock.Names.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(distributedLock.Names, name => name.Contains(firstTenant.ToString("D"), StringComparison.Ordinal) && name.Contains("notifications-client", StringComparison.Ordinal) && name.Contains("notifications.read", StringComparison.Ordinal));
        Assert.Contains(distributedLock.Names, name => name.Contains(secondTenant.ToString("D"), StringComparison.Ordinal));
        Assert.Contains(distributedLock.Names, name => name.Contains("notifications.write", StringComparison.Ordinal));
        Assert.Contains(distributedLock.Names, name => name.Contains("auditlogs-client", StringComparison.Ordinal));
        Assert.Equal(4, cache.SetKeys.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(cache.SetKeys, key => key.Contains(firstTenant.ToString("D"), StringComparison.Ordinal) && key.Contains("notifications-client", StringComparison.Ordinal) && key.Contains("notifications.read", StringComparison.Ordinal));
        Assert.Contains(cache.SetKeys, key => key.Contains(secondTenant.ToString("D"), StringComparison.Ordinal));
        Assert.Contains(cache.SetKeys, key => key.Contains("notifications.write", StringComparison.Ordinal));
        Assert.Contains(cache.SetKeys, key => key.Contains("auditlogs-client", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Tenant_bound_request_includes_tenant_and_the_tenantless_overload_stays_tenantless()
    {
        var forms = new List<string>();
        var cache = new RecordingCache();
        var distributedLock = new RecordingDistributedLock();
        var sut = CreateProvider(
            new RecordingHttpClientFactory(async (request, cancellationToken) =>
            {
                forms.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
                return TokenResponse();
            }),
            cache,
            distributedLock);
        var tenantId = Guid.CreateVersion7();

        Assert.Equal(Token, await sut.GetAccessTokenForClientAsync("Notifications", "notifications.read", tenantId));
        Assert.Equal(Token, await sut.GetAccessTokenForClientAsync("Notifications", "legacy.read"));

        Assert.Equal(2, forms.Count);
        Assert.Contains($"tenant_id={Uri.EscapeDataString(tenantId.ToString("D"))}", forms[0], StringComparison.Ordinal);
        Assert.DoesNotContain("tenant_id=", forms[1], StringComparison.Ordinal);
        Assert.DoesNotContain(tenantId.ToString("D"), distributedLock.Names[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Contract_offers_no_implicit_default_client_profile()
    {
        // A host configured only with its own profile must never be able to fall into a profile it does not have.
        Assert.DoesNotContain(
            typeof(IInternalAccessTokenProvider).GetMethods(),
            method => method.GetParameters().All(parameter => parameter.Name != "clientProfileName"));
        Assert.Null(typeof(InternalAccessTokenProvider).GetField("DefaultClientProfileName"));
    }

    [Fact]
    public async Task Tenant_bound_request_fails_closed_when_tenant_is_missing()
    {
        var cache = new RecordingCache();
        var distributedLock = new RecordingDistributedLock();
        var http = new RecordingHttpClientFactory((_, _) => throw new Xunit.Sdk.XunitException("HTTP must not be called."));
        var sut = CreateProvider(http, cache, distributedLock);

        var token = await sut.GetAccessTokenForClientAsync("Notifications", "notifications.read", Guid.Empty);

        Assert.Null(token);
        Assert.Empty(cache.GetKeys);
        Assert.Empty(distributedLock.Names);
        Assert.Equal(0, http.RequestCount);
    }

    [Theory]
    [InlineData("non-success")]
    [InlineData("malformed")]
    [InlineData("missing-token")]
    public async Task Tenant_bound_request_fails_closed_when_token_response_is_invalid(string variation)
    {
        var logger = new RecordingLogger<InternalAccessTokenProvider>();
        var http = new RecordingHttpClientFactory((_, _) => Task.FromResult(variation switch
        {
            "non-success" => new HttpResponseMessage(HttpStatusCode.BadGateway),
            "malformed" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{invalid-json") },
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"expires_in\":3600}") }
        }));
        var sut = CreateProvider(http, new RecordingCache(), new RecordingDistributedLock(), logger);

        var token = await sut.GetAccessTokenForClientAsync("Notifications", "notifications.read", Guid.CreateVersion7());

        Assert.Null(token);
        Assert.DoesNotContain(logger.Messages, message => message.Contains(ClientSecret, StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, message => message.Contains(Token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Tenant_bound_request_propagates_caller_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var sut = CreateProvider(
            new RecordingHttpClientFactory((_, _) => throw new Xunit.Sdk.XunitException("HTTP must not be called.")),
            new RecordingCache(),
            new RecordingDistributedLock());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sut.GetAccessTokenForClientAsync("Notifications", "notifications.read", Guid.CreateVersion7(), cancellation.Token));
    }

    private static InternalAccessTokenProvider CreateProvider(
        IHttpClientFactory httpClientFactory,
        RecordingCache cache,
        RecordingDistributedLock distributedLock,
        ILogger<InternalAccessTokenProvider>? logger = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AuthServer:Authority"] = Authority,
                ["OpenIddict:Clients:Internal:ClientId"] = "internal-client",
                ["OpenIddict:Clients:Internal:Secret"] = ClientSecret,
                ["OpenIddict:Clients:Notifications:ClientId"] = "notifications-client",
                ["OpenIddict:Clients:Notifications:Secret"] = ClientSecret,
                ["OpenIddict:Clients:AuditLogs:ClientId"] = "auditlogs-client",
                ["OpenIddict:Clients:AuditLogs:Secret"] = ClientSecret
            })
            .Build();

        return new InternalAccessTokenProvider(
            httpClientFactory,
            configuration,
            cache,
            distributedLock,
            logger ?? new RecordingLogger<InternalAccessTokenProvider>());
    }

    private static HttpResponseMessage TokenResponse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("{\"access_token\":\"" + Token + "\",\"expires_in\":3600}")
    };

    private sealed class RecordingHttpClientFactory(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : IHttpClientFactory, IDisposable
    {
        private readonly RecordingHttpMessageHandler _handler = new(send);

        public int RequestCount => _handler.RequestCount;

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);

        public void Dispose() => _handler.Dispose();
    }

    private sealed class RecordingHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return send(request, cancellationToken);
        }
    }

    private sealed class RecordingCache : IDistributedCache<InternalAccessTokenCacheItem, string>
    {
        private readonly Dictionary<string, InternalAccessTokenCacheItem> _items = new(StringComparer.Ordinal);

        public List<string> GetKeys { get; } = [];
        public List<string> SetKeys { get; } = [];

        public InternalAccessTokenCacheItem? Get(string key, bool? hideErrors = null, bool considerUow = false) =>
            _items.GetValueOrDefault(key);

        public KeyValuePair<string, InternalAccessTokenCacheItem?>[] GetMany(
            IEnumerable<string> keys,
            bool? hideErrors = null,
            bool considerUow = false) => keys.Select(key => new KeyValuePair<string, InternalAccessTokenCacheItem?>(key, Get(key))).ToArray();

        public Task<KeyValuePair<string, InternalAccessTokenCacheItem?>[]> GetManyAsync(
            IEnumerable<string> keys,
            bool? hideErrors = null,
            bool considerUow = false,
            CancellationToken token = default) => Task.FromResult(GetMany(keys, hideErrors, considerUow));

        public Task<InternalAccessTokenCacheItem?> GetAsync(
            string key,
            bool? hideErrors = null,
            bool considerUow = false,
            CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            GetKeys.Add(key);
            return Task.FromResult(Get(key, hideErrors, considerUow));
        }

        public InternalAccessTokenCacheItem? GetOrAdd(
            string key,
            Func<InternalAccessTokenCacheItem> factory,
            Func<DistributedCacheEntryOptions>? optionsFactory = null,
            bool? hideErrors = null,
            bool considerUow = false) => _items.GetValueOrDefault(key) ?? factory();

        public Task<InternalAccessTokenCacheItem?> GetOrAddAsync(
            string key,
            Func<Task<InternalAccessTokenCacheItem>> factory,
            Func<DistributedCacheEntryOptions>? optionsFactory = null,
            bool? hideErrors = null,
            bool considerUow = false,
            CancellationToken token = default) => Task.FromResult<InternalAccessTokenCacheItem?>(_items.GetValueOrDefault(key));

        public KeyValuePair<string, InternalAccessTokenCacheItem?>[] GetOrAddMany(
            IEnumerable<string> keys,
            Func<IEnumerable<string>, List<KeyValuePair<string, InternalAccessTokenCacheItem>>> factory,
            Func<DistributedCacheEntryOptions>? optionsFactory = null,
            bool? hideErrors = null,
            bool considerUow = false) => GetMany(keys, hideErrors, considerUow);

        public Task<KeyValuePair<string, InternalAccessTokenCacheItem?>[]> GetOrAddManyAsync(
            IEnumerable<string> keys,
            Func<IEnumerable<string>, Task<List<KeyValuePair<string, InternalAccessTokenCacheItem>>>> factory,
            Func<DistributedCacheEntryOptions>? optionsFactory = null,
            bool? hideErrors = null,
            bool considerUow = false,
            CancellationToken token = default) => Task.FromResult(GetMany(keys, hideErrors, considerUow));

        public void Set(string key, InternalAccessTokenCacheItem value, DistributedCacheEntryOptions? options = null, bool? hideErrors = null, bool considerUow = false)
        {
            _items[key] = value;
            SetKeys.Add(key);
        }

        public Task SetAsync(
            string key,
            InternalAccessTokenCacheItem value,
            DistributedCacheEntryOptions? options = null,
            bool? hideErrors = null,
            bool considerUow = false,
            CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            Set(key, value, options, hideErrors, considerUow);
            return Task.CompletedTask;
        }

        public void SetMany(IEnumerable<KeyValuePair<string, InternalAccessTokenCacheItem>> items, DistributedCacheEntryOptions? options = null, bool? hideErrors = null, bool considerUow = false)
        {
            foreach (var item in items)
            {
                Set(item.Key, item.Value, options, hideErrors, considerUow);
            }
        }

        public Task SetManyAsync(IEnumerable<KeyValuePair<string, InternalAccessTokenCacheItem>> items, DistributedCacheEntryOptions? options = null, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            SetMany(items, options, hideErrors, considerUow);
            return Task.CompletedTask;
        }

        public void Refresh(string key, bool? hideErrors = null) { }
        public Task RefreshAsync(string key, bool? hideErrors = null, CancellationToken token = default) => Task.CompletedTask;
        public void RefreshMany(IEnumerable<string> keys, bool? hideErrors = null) { }
        public Task RefreshManyAsync(IEnumerable<string> keys, bool? hideErrors = null, CancellationToken token = default) => Task.CompletedTask;
        public void Remove(string key, bool? hideErrors = null, bool considerUow = false) => _items.Remove(key);
        public Task RemoveAsync(string key, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
        {
            Remove(key, hideErrors, considerUow);
            return Task.CompletedTask;
        }

        public void RemoveMany(IEnumerable<string> keys, bool? hideErrors = null, bool considerUow = false)
        {
            foreach (var key in keys)
            {
                Remove(key, hideErrors, considerUow);
            }
        }

        public Task RemoveManyAsync(IEnumerable<string> keys, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
        {
            RemoveMany(keys, hideErrors, considerUow);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingDistributedLock : IDistributedLock
    {
        public List<string> Names { get; } = [];

        public Task<IDistributedLockHandle?> TryAcquireAsync(
            string name,
            DistributedLockAcquireOptions options,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Names.Add(name);
            return Task.FromResult<IDistributedLockHandle?>(new LockHandle(name));
        }

        public Task<IDistributedLockHandle> AcquireAsync(
            string name,
            DistributedLockAcquireOptions options,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class LockHandle(string name) : IDistributedLockHandle
    {
        public string Name { get; } = name;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            if (exception != null)
            {
                Messages.Add(exception.ToString());
            }
        }
    }
}
