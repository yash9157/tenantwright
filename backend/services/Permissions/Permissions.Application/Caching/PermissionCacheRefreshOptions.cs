namespace Permissions.Application.Caching;

public sealed class PermissionCacheRefreshOptions
{
    public const string SectionName = "PermissionCacheRefresh";

    public bool Enabled { get; set; } = true;
    public string RecurringCron { get; set; } = "*/30 * * * *";
    public string Queue { get; set; } = "permissions-recurring";
}

public static class PermissionCacheRefreshOptionsPostConfigure
{
    public static void Normalize(PermissionCacheRefreshOptions options)
    {
        options.RecurringCron = string.IsNullOrWhiteSpace(options.RecurringCron)
            ? "*/30 * * * *"
            : options.RecurringCron.Trim();
        options.Queue = string.IsNullOrWhiteSpace(options.Queue)
            ? "permissions-recurring"
            : options.Queue.Trim();
    }
}
