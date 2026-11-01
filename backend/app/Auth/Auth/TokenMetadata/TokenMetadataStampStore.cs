using Auth.Caching;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;
using Shared.Modularity.DependencyInjection;

namespace Auth.TokenMetadata;

public interface ITokenMetadataStampStore
{
    Task<string> GetCurrentStampAsync(CancellationToken cancellationToken = default);

    Task<string> BumpStampAsync(CancellationToken cancellationToken = default);
}

public sealed class TokenMetadataStampStore : ITokenMetadataStampStore, ISingletonDependency
{
    private readonly IAuthHybridCache<TokenMetadataStampCacheItem> _stampCache;
    private readonly TokenMetadataCacheOptions _options;
    private readonly ILogger<TokenMetadataStampStore> _logger;

    public TokenMetadataStampStore(
        IAuthHybridCache<TokenMetadataStampCacheItem> stampCache,
        IOptions<TokenMetadataCacheOptions> options,
        ILogger<TokenMetadataStampStore> logger)
    {
        _stampCache = stampCache;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string> GetCurrentStampAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return "disabled";
        }

        var distributedStampKey = BuildStampCacheKey();

        try
        {
            var distributedStamp = await _stampCache.GetAsync(distributedStampKey, cancellationToken);
            if (distributedStamp != null && !string.IsNullOrWhiteSpace(distributedStamp.Value))
            {
                return distributedStamp.Value;
            }

            var createdStamp = Guid.NewGuid().ToString("N");
            await _stampCache.SetAsync(
                distributedStampKey,
                new TokenMetadataStampCacheItem { Value = createdStamp },
                options: CreateOptions(_options.StampCacheMinutes),
                token: cancellationToken);
            return createdStamp;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Token metadata stamp resolution failed.");
            throw new InvalidOperationException("Token metadata stamp resolution failed.", ex);
        }
    }

    public async Task<string> BumpStampAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return "disabled";
        }

        var nextStamp = Guid.NewGuid().ToString("N");
        var distributedStampKey = BuildStampCacheKey();

        try
        {
            await _stampCache.SetAsync(
                distributedStampKey,
                new TokenMetadataStampCacheItem { Value = nextStamp },
                options: CreateOptions(_options.StampCacheMinutes),
                token: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Token metadata stamp update failed.");
            throw new InvalidOperationException("Token metadata stamp update failed.", ex);
        }

        return nextStamp;
    }

    private string BuildStampCacheKey()
        => $"{_options.KeyPrefix}:Stamp";

    private static HybridCacheEntryOptions CreateOptions(int minutes)
    {
        var ttl = TimeSpan.FromMinutes(Math.Max(1, minutes));
        return new HybridCacheEntryOptions
        {
            LocalCacheExpiration = ttl,
            Expiration = ttl
        };
    }
}
