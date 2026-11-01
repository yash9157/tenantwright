namespace Shared.DistributedLocking.Options;

public sealed class DistributedLockingOptions
{
    public const string SectionName = "DistributedLocking";

    public string Provider { get; set; } = string.Empty;
    public string KeyPrefix { get; set; } = string.Empty;
    public ValkeyDistributedLockingOptions Valkey { get; set; } = new();
    public DistributedLockPolicyOptions DefaultPolicy { get; set; } = new();
    public Dictionary<string, DistributedLockPolicyOptions> Policies { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ValkeyDistributedLockingOptions
{
    public string? Configuration { get; set; }
    public int Database { get; set; } = -1;
    public int BusyWaitMinMs { get; set; } = 25;
    public int BusyWaitMaxMs { get; set; } = 100;
}
