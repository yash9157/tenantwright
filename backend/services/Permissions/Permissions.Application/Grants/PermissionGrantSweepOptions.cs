namespace Permissions.Application.Grants;

/// <summary>
/// Config for the periodic sweep that removes expired <c>PermissionGrant</c> rows and invalidates the
/// affected providers' caches. Bound from <c>Permissions:GrantSweep</c>.
/// </summary>
public sealed class PermissionGrantSweepOptions
{
    public const string SectionName = "Permissions:GrantSweep";

    public bool Enabled { get; set; } = true;

    /// <summary>How often the sweep runs.</summary>
    public int IntervalSeconds { get; set; } = 300;

    /// <summary>Maximum rows deleted per statement; the sweep loops until exhausted.</summary>
    public int BatchSize { get; set; } = 500;
}
