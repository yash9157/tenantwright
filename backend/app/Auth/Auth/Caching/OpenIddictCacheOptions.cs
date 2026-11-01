namespace Auth.Caching;

public sealed class OpenIddictCacheOptions
{
    public int ApplicationTtlMinutes { get; set; } = 1440;
    public int ScopeTtlMinutes { get; set; } = 1440;
}
