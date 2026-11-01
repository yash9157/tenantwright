namespace Auth.Registration;

public sealed class PublicTenantRegistrationOptions
{
    private const string CloudflareAlwaysPassTestSiteKey = "1x00000000000000000000AA";
    private const string CloudflareAlwaysPassTestSecret = "1x0000000000000000000000000000000AA";

    public bool Enabled { get; set; }
    public string TurnstileSiteKey { get; set; } = string.Empty;
    public string TurnstileSecret { get; set; } = string.Empty;
    public string ExpectedHostName { get; set; } = string.Empty;
    public int CleanupHours { get; set; } = 72;
    public int StatusTokenLifetimeHours { get; set; } = 168;
    public int IpHourlyLimit { get; set; } = 3;
    public int EmailHourlyLimit { get; set; } = 2;
    public int TenantDailyLimit { get; set; } = 2;
    public string HashKey { get; set; } = string.Empty;
    public List<string> BlockedDomains { get; set; } = [];

    public bool UsesCloudflareTestCredentials()
    {
        return string.Equals(TurnstileSiteKey, CloudflareAlwaysPassTestSiteKey, StringComparison.Ordinal)
            || string.Equals(TurnstileSecret, CloudflareAlwaysPassTestSecret, StringComparison.Ordinal);
    }
}
