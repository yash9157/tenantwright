using System.Runtime.CompilerServices;
using Auth.Caching;
using Auth.TokenMetadata;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Auth.Tests;

public sealed class TokenMetadataCacheServiceTests
{
    [Fact]
    public async Task GetResourcesForScopesAsync_UsesScopeSetCache()
    {
        var source = new FakeTokenMetadataSource(new Dictionary<string, string[]>
        {
            ["scope-a"] = ["res-a"],
            ["scope-b"] = ["res-b"]
        });
        var stampStore = new FakeTokenMetadataStampStore("stamp-1");
        var scopeSetCache = new FakeAuthHybridCache<TokenScopeSetResourcesCacheItem>();
        var scopeCache = new FakeAuthHybridCache<TokenScopeResourcesCacheItem>();
        var clientCache = new FakeAuthHybridCache<TokenClientMetadataCacheItem>();
        var service = CreateService(source, stampStore, scopeSetCache, scopeCache, clientCache);

        var first = await service.GetResourcesForScopesAsync(["scope-a", "scope-b"]);
        var second = await service.GetResourcesForScopesAsync(["scope-b", "scope-a"]);

        Assert.Equal(2, source.ScopeResourceReadCount);
        Assert.Equal(1, scopeSetCache.SetCount);
        Assert.True(scopeSetCache.GetCount >= 2);
        Assert.True(
            first.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(["res-a", "res-b"], StringComparer.Ordinal));
        Assert.True(
            second.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(["res-a", "res-b"], StringComparer.Ordinal));
    }

    [Fact]
    public async Task InvalidateScopeAsync_BumpedStamp_InvalidatesScopeSetAndScopeReads()
    {
        var source = new FakeTokenMetadataSource(new Dictionary<string, string[]>
        {
            ["scope-a"] = ["res-a"],
            ["scope-b"] = ["res-b"]
        });
        var stampStore = new FakeTokenMetadataStampStore("stamp-1");
        var scopeSetCache = new FakeAuthHybridCache<TokenScopeSetResourcesCacheItem>();
        var scopeCache = new FakeAuthHybridCache<TokenScopeResourcesCacheItem>();
        var clientCache = new FakeAuthHybridCache<TokenClientMetadataCacheItem>();
        var service = CreateService(source, stampStore, scopeSetCache, scopeCache, clientCache);

        _ = await service.GetResourcesForScopesAsync(["scope-a", "scope-b"]);
        await service.InvalidateScopeAsync("scope-a");
        _ = await service.GetResourcesForScopesAsync(["scope-a", "scope-b"]);

        Assert.Equal(1, stampStore.BumpCount);
        Assert.Equal(4, source.ScopeResourceReadCount);
        Assert.True(scopeCache.RemoveCount >= 1);
    }

    private static TokenMetadataCacheService CreateService(
        IOpenIddictTokenMetadataSource source,
        ITokenMetadataStampStore stampStore,
        IAuthHybridCache<TokenScopeSetResourcesCacheItem> scopeSetCache,
        IAuthHybridCache<TokenScopeResourcesCacheItem> scopeCache,
        IAuthHybridCache<TokenClientMetadataCacheItem> clientCache)
    {
        var options = Options.Create(new TokenMetadataCacheOptions
        {
            Enabled = true,
            KeyPrefix = "Auth:TokenMetadata",
            ScopeResourceL1Seconds = 30,
            ScopeResourceL2Minutes = 10,
            ScopeSetL1Seconds = 30,
            ScopeSetL2Minutes = 10,
            ClientL1Seconds = 30,
            ClientL2Minutes = 10,
            StampCacheMinutes = 10
        });

        return new TokenMetadataCacheService(
            source,
            stampStore,
            scopeSetCache,
            scopeCache,
            clientCache,
            options,
            NullLogger<TokenMetadataCacheService>.Instance);
    }

    private sealed class FakeTokenMetadataSource : IOpenIddictTokenMetadataSource
    {
        private readonly IReadOnlyDictionary<string, string[]> _scopeResources;

        public int ScopeResourceReadCount { get; private set; }

        public FakeTokenMetadataSource(IReadOnlyDictionary<string, string[]> scopeResources)
        {
            _scopeResources = scopeResources;
        }

        public Task<TokenClientMetadataCacheItem?> GetClientAsync(string clientId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<TokenClientMetadataCacheItem?>(null);
        }

        public Task<string[]> GetScopeResourcesAsync(string scopeName, CancellationToken cancellationToken = default)
        {
            ScopeResourceReadCount++;
            if (_scopeResources.TryGetValue(scopeName, out var resources))
            {
                return Task.FromResult(resources);
            }

            return Task.FromResult(Array.Empty<string>());
        }

        public async IAsyncEnumerable<string> ListClientIdsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public async IAsyncEnumerable<string> ListScopeNamesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class FakeTokenMetadataStampStore : ITokenMetadataStampStore
    {
        private string _currentStamp;

        public int BumpCount { get; private set; }

        public FakeTokenMetadataStampStore(string initialStamp)
        {
            _currentStamp = initialStamp;
        }

        public Task<string> GetCurrentStampAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_currentStamp);
        }

        public Task<string> BumpStampAsync(CancellationToken cancellationToken = default)
        {
            BumpCount++;
            _currentStamp = $"stamp-{BumpCount + 1}";
            return Task.FromResult(_currentStamp);
        }
    }

    private sealed class FakeAuthHybridCache<TCacheItem> : IAuthHybridCache<TCacheItem>
        where TCacheItem : class
    {
        private readonly Dictionary<string, TCacheItem> _values = new(StringComparer.Ordinal);

        public int GetCount { get; private set; }

        public int SetCount { get; private set; }

        public int RemoveCount { get; private set; }

        public Task<TCacheItem?> GetAsync(string key, CancellationToken token = default)
        {
            GetCount++;
            _values.TryGetValue(key, out var item);
            return Task.FromResult(item);
        }

        public Task SetAsync(
            string key,
            TCacheItem value,
            HybridCacheEntryOptions? options = null,
            CancellationToken token = default)
        {
            SetCount++;
            _values[key] = value;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key, CancellationToken token = default)
        {
            RemoveCount++;
            _values.Remove(key);
            return Task.CompletedTask;
        }

        public Task RemoveManyAsync(IEnumerable<string> keys, CancellationToken token = default)
        {
            foreach (var key in keys)
            {
                _values.Remove(key);
            }

            return Task.CompletedTask;
        }
    }
}
