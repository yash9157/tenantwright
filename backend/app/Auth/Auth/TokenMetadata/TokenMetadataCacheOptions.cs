namespace Auth.TokenMetadata;

public sealed class TokenMetadataCacheOptions
{
    public bool Enabled { get; set; } = true;

    public string KeyPrefix { get; set; } = "Auth:TokenMetadata";

    public int ScopeResourceL1Seconds { get; set; } = 60;

    public int ScopeResourceL2Minutes { get; set; } = 30;

    public int ScopeSetL1Seconds { get; set; } = 60;

    public int ScopeSetL2Minutes { get; set; } = 30;

    public int ClientL1Seconds { get; set; } = 60;

    public int ClientL2Minutes { get; set; } = 30;

    public int StampCacheMinutes { get; set; } = 1440;
}
