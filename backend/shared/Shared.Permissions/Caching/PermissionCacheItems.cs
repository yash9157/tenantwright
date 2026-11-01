namespace Shared.Permissions.Caching;

public class PermissionProviderStampCacheItem
{
    public long Stamp { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public class PermissionDecisionCacheItem
{
    public long Stamp { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public bool IsGranted { get; set; }
    public int Source { get; set; }
}

public class PermissionProviderGrantsCacheItem
{
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public string[] AllowExact { get; set; } = Array.Empty<string>();
    public string[] DenyExact { get; set; } = Array.Empty<string>();
    public string[] AllowWildcard { get; set; } = Array.Empty<string>();
    public string[] DenyWildcard { get; set; } = Array.Empty<string>();
}

public class FeatureValueCacheItem
{
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public string Value { get; set; } = "";
}

public class PermissionSnapshotCacheItem
{
    public long Stamp { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }

    public string[] UserAllowExact { get; set; } = Array.Empty<string>();
    public string[] UserDenyExact { get; set; } = Array.Empty<string>();
    public string[] UserAllowWildcard { get; set; } = Array.Empty<string>();
    public string[] UserDenyWildcard { get; set; } = Array.Empty<string>();

    public string[] RoleAllowExact { get; set; } = Array.Empty<string>();
    public string[] RoleDenyExact { get; set; } = Array.Empty<string>();
    public string[] RoleAllowWildcard { get; set; } = Array.Empty<string>();
    public string[] RoleDenyWildcard { get; set; } = Array.Empty<string>();
}
