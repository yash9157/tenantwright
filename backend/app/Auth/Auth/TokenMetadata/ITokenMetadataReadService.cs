namespace Auth.TokenMetadata;

public interface ITokenMetadataReadService
{
    Task<IReadOnlyList<string>> GetResourcesForScopesAsync(IEnumerable<string> scopes, CancellationToken cancellationToken = default);

    Task<TokenClientMetadataCacheItem?> GetClientAsync(string clientId, CancellationToken cancellationToken = default);
}

public interface ITokenMetadataInvalidationService
{
    Task InvalidateClientAsync(string clientId, CancellationToken cancellationToken = default);

    Task InvalidateScopeAsync(string scopeName, CancellationToken cancellationToken = default);

    Task InvalidateAllAsync(string reason, CancellationToken cancellationToken = default);
}

public interface ITokenMetadataWarmupService
{
    Task WarmupAsync(CancellationToken cancellationToken = default);

    Task WarmupCommonScopeSetsAsync(
        IEnumerable<IEnumerable<string>> scopeSets,
        CancellationToken cancellationToken = default);
}

public interface IOpenIddictTokenMetadataSource
{
    Task<TokenClientMetadataCacheItem?> GetClientAsync(string clientId, CancellationToken cancellationToken = default);

    Task<string[]> GetScopeResourcesAsync(string scopeName, CancellationToken cancellationToken = default);

    IAsyncEnumerable<string> ListClientIdsAsync(CancellationToken cancellationToken = default);

    IAsyncEnumerable<string> ListScopeNamesAsync(CancellationToken cancellationToken = default);
}
