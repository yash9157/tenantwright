namespace Auth.Clients.Caching;

public sealed class IdentityUserCacheOptions
{
    public int ProfileSoftTtlMinutes { get; set; } = 120;
    public int ProfileHardTtlMinutes { get; set; } = 24 * 60;
    public int AliasSoftTtlMinutes { get; set; } = 6 * 60;
    public int AliasHardTtlMinutes { get; set; } = 24 * 60;
    public int AuthzHardTtlUpperBoundMinutes { get; set; } = 15;
    public int AuthzSoftTtlMinMinutes { get; set; } = 2;
    public int AuthzSoftTtlMaxMinutes { get; set; } = 5;
    public int LockTtlSeconds { get; set; } = 3;
    public int StrictRefreshWaitMilliseconds { get; set; } = 300;
    public int JitterPercent { get; set; } = 10;
}
