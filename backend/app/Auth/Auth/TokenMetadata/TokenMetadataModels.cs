namespace Auth.TokenMetadata;

public sealed class TokenScopeResourcesCacheItem
{
    public string ScopeName { get; set; } = string.Empty;

    public string[] Resources { get; set; } = [];
}

public sealed class TokenScopeSetResourcesCacheItem
{
    public string ScopeSetHash { get; set; } = string.Empty;

    public string[] ScopeNames { get; set; } = [];

    public string[] Resources { get; set; } = [];
}

public sealed class TokenClientMetadataCacheItem
{
    public string ClientId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string ClientType { get; set; } = string.Empty;

    public string ConsentType { get; set; } = string.Empty;

    public string[] Permissions { get; set; } = [];

    public string[] RedirectUris { get; set; } = [];

    public string[] PostLogoutRedirectUris { get; set; } = [];
}

public sealed class TokenMetadataStampCacheItem
{
    public string Value { get; set; } = string.Empty;
}
