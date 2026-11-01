namespace Auth.RateLimiting;

public enum TokenRateLimitPartitionMode
{
    ClientIp = 0,
    IpOnly = 1
}

public sealed class TokenRateLimitingOptions
{
    // Per client+IP, per window. 120/min is ample for legitimate refresh_token and
    // authorization_code exchanges from a single client+IP while substantially
    // reducing password-grant brute-force throughput. Override via configuration.
    public int PermitLimit { get; set; } = 120;

    public int WindowSeconds { get; set; } = 60;

    public int QueueLimit { get; set; }

    public TokenRateLimitPartitionMode PartitionMode { get; set; } = TokenRateLimitPartitionMode.ClientIp;
}
